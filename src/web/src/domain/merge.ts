import { isStorable, lowerShared } from './text'
import type { Priority, Status, TagState, Task, TodoChange } from './types'

// Merge last-write-wins por campo (escopo, seção 5), idêntico a src/Way2Do.Api/Domain/TodoMerge.cs. Casos
// compartilhados em tests/fixtures/merge.json. A alteração é validada inteira antes de qualquer escrita: se for
// recusada, a tarefa fica intocada.

export const LIMITS = { maxTitle: 200, maxDescription: 4000, maxTag: 40, maxTags: 20 } as const

export const FIELD_NAMES = ['title', 'description', 'dueDate', 'priority', 'status'] as const
export type FieldName = (typeof FIELD_NAMES)[number]

export interface MergeResult {
  /** Algo precisa ser gravado (inclui só metadados, como um timestamp que avançou). */
  changed: boolean
  /** Algo visível mudou. */
  contentChanged: boolean
}

export class InvalidChangeError extends Error {
  readonly field: string
  readonly reason: string

  constructor(field: string, reason: string) {
    super(`${field}: ${reason}`)
    this.field = field
    this.reason = reason
  }
}

const NONE: MergeResult = { changed: false, contentChanged: false }

/** Aplica a alteração sobre `target` (mutando-o). `now` é o relógio de quem aplica. */
export function applyChange(target: Task, change: TodoChange, now: string): MergeResult {
  const nowMs = Date.parse(now)

  // 1) Valida e normaliza toda a alteração, sem tocar na tarefa.
  const fields = Object.entries(change.fields ?? {}).map(([name, f]) => ({
    name: name as FieldName,
    value: parse(name, f.value),
    ts: clamp(f.ts, nowMs),
  }))
  const tags = normalizeTags(change.tags ?? {}, nowMs)
  const deletedAt = change.deletedAt ? clamp(change.deletedAt, nowMs) : null
  const isNew = target.createdAt === null && Object.keys(target.fieldTimestamps).length === 0

  // 2) Exclusão vence edição.
  if (deletedAt !== null && isNew) return NONE // nada a excluir
  if (target.deletedAt !== null) {
    if (deletedAt === null || ms(deletedAt) >= ms(target.deletedAt)) return NONE
    target.deletedAt = deletedAt // entre duas exclusões, fica a mais antiga
    return { changed: true, contentChanged: false }
  }
  if (deletedAt !== null) {
    target.deletedAt = deletedAt
    return { changed: true, contentChanged: true }
  }

  // 3) Decide os vencedores, ainda sem escrever.
  const winningFields = fields.filter((f) => fieldWins(target, f.name, f.value, f.ts))
  const winningTags = [...tags].filter(([name, tag]) => tagWins(target, name, tag))

  // 4) Confere o resultado.
  if (isNew && !winningFields.some((f) => f.name === 'title')) throw new InvalidChangeError('title', 'obrigatório')
  const visible = new Set(Object.entries(target.tags).filter(([, t]) => t.present).map(([name]) => name))
  for (const [name, tag] of winningTags) {
    if (tag.present) visible.add(name)
    else visible.delete(name)
  }
  if (visible.size > LIMITS.maxTags) throw new InvalidChangeError('tags', `no máximo ${LIMITS.maxTags} tags`)

  // 5) Aplica.
  let changed = false
  let contentChanged = false
  if (isNew) {
    target.createdAt = change.createdAt ? clamp(change.createdAt, nowMs) : toIso(nowMs)
    changed = contentChanged = true
  }

  for (const { name, value, ts } of winningFields) {
    if (target.fieldTimestamps[name] === undefined || ms(target.fieldTimestamps[name]) !== ms(ts)) {
      target.fieldTimestamps[name] = ts
      changed = true
    }
    if (value !== canonical(target, name)) {
      assign(target, name, value)
      changed = contentChanged = true
    }
  }

  for (const [name, tag] of winningTags) {
    const wasVisible = ownTag(target.tags, name)?.present === true
    if (wasVisible !== tag.present) contentChanged = true
    setTag(target.tags, name, tag)
    changed = true
  }

  return { changed, contentChanged }
}

/** Identidade da tag: NFC, sem espaços nas pontas, minúsculas (o mesmo NormalizeTag do servidor). */
export function normalizeTag(tag: string): string {
  if (!isStorable(tag)) throw new InvalidChangeError('tags', 'caractere inválido')
  const name = lowerShared(tag.normalize('NFC').trim())
  if (name.length === 0) throw new InvalidChangeError('tags', 'tag vazia')
  if (name.length > LIMITS.maxTag) throw new InvalidChangeError('tags', `tag com mais de ${LIMITS.maxTag} caracteres`)
  return name
}

/** Tags visíveis: as presentes, em ordem. */
export function visibleTags(task: Task): string[] {
  return Object.entries(task.tags)
    .filter(([, t]) => t.present)
    .map(([name]) => name)
    .sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))
}

function fieldWins(target: Task, name: FieldName, value: string | null, ts: string): boolean {
  const current = target.fieldTimestamps[name]
  if (current === undefined || ms(ts) > ms(current)) return true
  return ms(ts) === ms(current) && compareValues(value, canonical(target, name)) > 0
}

function tagWins(target: Task, name: string, incoming: TagState): boolean {
  const current = ownTag(target.tags, name)
  if (!current) return true
  return ms(incoming.ts) > ms(current.ts) || (ms(incoming.ts) === ms(current.ts) && incoming.present && !current.present)
}

function normalizeTags(tags: Record<string, TagState>, nowMs: number): Map<string, TagState> {
  const result = new Map<string, TagState>()
  for (const [raw, tag] of Object.entries(tags)) {
    const name = normalizeTag(raw)
    const incoming = { present: tag.present, ts: clamp(tag.ts, nowMs) }
    const other = result.get(name)
    if (!other || ms(incoming.ts) > ms(other.ts) || (ms(incoming.ts) === ms(other.ts) && incoming.present && !other.present))
      result.set(name, incoming)
  }
  return result
}

// Tags são chaves livres digitadas pelo usuário: "constructor" ou "__proto__" não podem ler propriedades herdadas nem
// trocar o protótipo do objeto. Leitura só de propriedade própria; escrita por defineProperty (não aciona o setter
// de __proto__).
export function ownTag(tags: Record<string, TagState>, name: string): TagState | undefined {
  return Object.hasOwn(tags, name) ? tags[name] : undefined
}

export function setTag(tags: Record<string, TagState>, name: string, tag: TagState): void {
  Object.defineProperty(tags, name, { value: tag, enumerable: true, writable: true, configurable: true })
}

// Nulo fica abaixo de qualquer valor; entre valores, ordem de código UTF-16 (a mesma do string.CompareOrdinal).
function compareValues(a: string | null, b: string | null): number {
  if (a === b) return 0
  if (a === null) return -1
  if (b === null) return 1
  return a < b ? -1 : 1
}

function canonical(t: Task, name: FieldName): string | null {
  switch (name) {
    case 'title':
      return t.title
    case 'description':
      return t.description
    case 'dueDate':
      return t.dueDate
    case 'priority':
      return t.priority
    case 'status':
      return t.status
  }
}

function assign(t: Task, name: FieldName, value: string | null): void {
  switch (name) {
    case 'title':
      t.title = value!
      break
    case 'description':
      t.description = value
      break
    case 'dueDate':
      t.dueDate = value
      break
    case 'priority':
      t.priority = value as Priority
      break
    case 'status':
      t.status = value as Status
      break
  }
}

function parse(name: string, value: unknown): string | null {
  if (value !== null && typeof value !== 'string') throw new InvalidChangeError(name, 'valor precisa ser texto ou nulo')
  if (value !== null && !isStorable(value)) throw new InvalidChangeError(name, 'caractere inválido')

  switch (name) {
    case 'title':
      if (value === null || value.trim().length === 0) throw new InvalidChangeError(name, 'obrigatório')
      if (value.length > LIMITS.maxTitle) throw new InvalidChangeError(name, `no máximo ${LIMITS.maxTitle} caracteres`)
      return value
    case 'description':
      if (value !== null && value.length > LIMITS.maxDescription)
        throw new InvalidChangeError(name, `no máximo ${LIMITS.maxDescription} caracteres`)
      return value
    case 'dueDate':
      if (value === null) return null
      if (!isCalendarDate(value)) throw new InvalidChangeError(name, 'data no formato AAAA-MM-DD')
      return value
    case 'priority':
      if (value === 'low' || value === 'medium' || value === 'high') return value
      throw new InvalidChangeError(name, 'low, medium ou high')
    case 'status':
      if (value === 'pending' || value === 'done') return value
      throw new InvalidChangeError(name, 'pending ou done')
    default:
      throw new InvalidChangeError(name, 'campo desconhecido')
  }
}

function isCalendarDate(text: string): boolean {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text)
  if (!match) return false
  const [y, m, d] = [Number(match[1]), Number(match[2]), Number(match[3])]
  const date = new Date(Date.UTC(y, m - 1, d))
  return date.getUTCFullYear() === y && date.getUTCMonth() === m - 1 && date.getUTCDate() === d
}

function ms(iso: string): number {
  const value = Date.parse(iso)
  if (Number.isNaN(value)) throw new InvalidChangeError('ts', 'horário inválido')
  return value
}

function toIso(msValue: number): string {
  return new Date(msValue).toISOString()
}

// Horários em milissegundos (a precisão do JS) e limitados ao relógio de quem aplica.
function clamp(ts: string, nowMs: number): string {
  return toIso(Math.min(ms(ts), nowMs))
}
