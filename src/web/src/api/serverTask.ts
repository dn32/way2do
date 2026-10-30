import { UUID_PATTERN } from '../data/ids'
import { FIELD_NAMES, setTag } from '../domain/merge'
import type { Priority, Status, TagState, Task } from '../domain/types'

/** Resposta do servidor fora do contrato: tratada como erro do servidor, nunca gravada no banco local. */
export class UnexpectedResponseError extends Error {}

const PRIORITIES: readonly string[] = ['low', 'medium', 'high']
const STATUSES: readonly string[] = ['pending', 'done']

/**
 * Converte a tarefa do push/pull (escopo, seção 8) para o formato do banco local, conferindo cada campo. Horários
 * viram ISO 8601 em UTC com milissegundos, o mesmo formato das edições locais.
 */
export function toLocalTask(raw: unknown): Task {
  const t = asObject(raw, 'tarefa')
  if (typeof t.id !== 'string' || !UUID_PATTERN.test(t.id)) throw new UnexpectedResponseError('tarefa sem id válido')

  const tags: Record<string, TagState> = {}
  for (const [name, state] of Object.entries(asObject(t.tags, 'tags'))) {
    const s = asObject(state, 'tag')
    if (typeof s.present !== 'boolean') throw new UnexpectedResponseError('tag sem estado')
    setTag(tags, name, { present: s.present, ts: iso(s.ts) })
  }

  const fieldTimestamps: Record<string, string> = {}
  for (const [name, ts] of Object.entries(asObject(t.fieldTimestamps, 'fieldTimestamps'))) {
    if (!(FIELD_NAMES as readonly string[]).includes(name)) throw new UnexpectedResponseError(`campo desconhecido: ${name}`)
    fieldTimestamps[name] = iso(ts)
  }

  return {
    id: t.id.toLowerCase(),
    title: text(t.title, 'title'),
    description: t.description === null ? null : text(t.description, 'description'),
    dueDate: t.dueDate === null ? null : text(t.dueDate, 'dueDate'),
    priority: oneOf(t.priority, PRIORITIES, 'priority') as Priority,
    status: oneOf(t.status, STATUSES, 'status') as Status,
    tags,
    fieldTimestamps,
    createdAt: iso(t.createdAt),
    deletedAt: t.deletedAt === null ? null : iso(t.deletedAt),
    version: count(t.version, 'version'),
    changeSeq: count(t.changeSeq, 'changeSeq'),
  }
}

function asObject(value: unknown, what: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new UnexpectedResponseError(`${what} inválido`)
  return value as Record<string, unknown>
}

function text(value: unknown, what: string): string {
  if (typeof value !== 'string') throw new UnexpectedResponseError(`${what} inválido`)
  return value
}

function oneOf(value: unknown, allowed: readonly string[], what: string): string {
  if (typeof value !== 'string' || !allowed.includes(value)) throw new UnexpectedResponseError(`${what} inválido`)
  return value
}

function count(value: unknown, what: string): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) throw new UnexpectedResponseError(`${what} inválido`)
  return value
}

function iso(value: unknown): string {
  const ms = typeof value === 'string' ? Date.parse(value) : NaN
  if (Number.isNaN(ms)) throw new UnexpectedResponseError('horário inválido')
  return new Date(ms).toISOString()
}
