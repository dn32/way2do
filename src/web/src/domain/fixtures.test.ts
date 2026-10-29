import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { applyChange, InvalidChangeError, normalizeTag, type MergeResult } from './merge'
import { sortTasks } from './ordering'
import { emptyTask, type Priority, type Status, type Task, type TodoChange } from './types'
import { isUrgent } from './urgency'

// Os mesmos casos que a suíte C# lê (tests/fixtures): as duas implementações precisam concordar em todos.
function load<T>(file: string): T {
  const path = fileURLToPath(new URL(`../../../../tests/fixtures/${file}`, import.meta.url))
  return JSON.parse(readFileSync(path, 'utf-8')) as T
}

const iso = (s: string) => new Date(s).toISOString()

describe('regra "urgente" (urgencia.json)', () => {
  const { casos } = load<{ casos: { titulo: string | null; descricao: string | null; urgente: boolean; motivo: string }[] }>(
    'urgencia.json',
  )

  it.each(casos.map((c) => [c.motivo, c] as const))('%s', (_motivo, c) => {
    expect(isUrgent(c.titulo, c.descricao), JSON.stringify(c)).toBe(c.urgente)
  })
})

describe('identidade da tag (tags.json)', () => {
  const { casos } = load<{ casos: { entrada: string; esperado: string | null; motivo: string }[] }>('tags.json')

  it.each(casos.map((c) => [c.motivo, c] as const))('%s', (_motivo, c) => {
    if (c.esperado === null) expect(() => normalizeTag(c.entrada)).toThrow(InvalidChangeError)
    else expect(normalizeTag(c.entrada)).toBe(c.esperado)
  })
})

describe('ordenação (ordenacao.json)', () => {
  interface FixtureTask {
    id: string
    titulo: string
    descricao: string | null
    status: Status
    prioridade: Priority
    vencimento: string | null
    criadaEm: string
  }
  const { cenarios } = load<{ cenarios: { nome: string; tarefas: FixtureTask[]; esperado: string[] }[] }>('ordenacao.json')

  it.each(cenarios.map((s) => [s.nome, s] as const))('%s', (_nome, s) => {
    const tasks: Task[] = s.tarefas.map((t) => ({
      ...emptyTask(t.id),
      title: t.titulo,
      description: t.descricao,
      status: t.status,
      priority: t.prioridade,
      dueDate: t.vencimento,
      createdAt: iso(t.criadaEm),
    }))

    expect(sortTasks(tasks).map((t) => t.id)).toEqual(s.esperado)
  })
})

describe('merge (merge.json)', () => {
  interface FixtureState {
    criadaEm: string
    campos: Record<string, { valor: string | null; ts: string }>
    tags: Record<string, { presente: boolean; ts: string }>
    excluidaEm: string | null
  }
  interface FixtureChange {
    criadaEm?: string
    campos?: Record<string, { valor: string | null; ts: string }>
    tags?: Record<string, { presente: boolean; ts: string }>
    excluidaEm?: string | null
  }
  interface Scenario {
    nome: string
    agora: string
    atual: FixtureState | null
    alteracao: FixtureChange
    esperado: FixtureState | null
    erro?: string
    mudou: boolean
    conteudoMudou: boolean
  }
  const { cenarios } = load<{ cenarios: Scenario[] }>('merge.json')

  // Monta o estado direto nas propriedades, sem passar pelo código sob teste.
  function toTask(state: FixtureState): Task {
    const task = emptyTask(crypto.randomUUID())
    task.createdAt = iso(state.criadaEm)
    for (const [name, f] of Object.entries(state.campos)) {
      ;(task as unknown as Record<string, unknown>)[name] = f.valor
      task.fieldTimestamps[name] = iso(f.ts)
    }
    for (const [name, t] of Object.entries(state.tags)) task.tags[name] = { present: t.presente, ts: iso(t.ts) }
    task.deletedAt = state.excluidaEm ? iso(state.excluidaEm) : null
    return task
  }

  function toChange(c: FixtureChange): TodoChange {
    return {
      createdAt: c.criadaEm ?? null,
      deletedAt: c.excluidaEm ?? null,
      fields: Object.fromEntries(Object.entries(c.campos ?? {}).map(([n, f]) => [n, { value: f.valor, ts: f.ts }])),
      tags: Object.fromEntries(Object.entries(c.tags ?? {}).map(([n, t]) => [n, { present: t.presente, ts: t.ts }])),
    }
  }

  it.each(cenarios.map((s) => [s.nome, s] as const))('%s', (_nome, s) => {
    const target = s.atual ? toTask(s.atual) : emptyTask(crypto.randomUUID())
    const change = toChange(s.alteracao)

    let result: MergeResult = { changed: false, contentChanged: false }
    if (s.erro) {
      let error: unknown
      try {
        applyChange(target, change, iso(s.agora))
      } catch (e) {
        error = e
      }
      expect(error).toBeInstanceOf(InvalidChangeError)
      expect((error as InvalidChangeError).field).toBe(s.erro)
    } else {
      result = applyChange(target, change, iso(s.agora))
    }

    if (s.esperado === null) {
      expect(target.createdAt).toBeNull()
      expect(target.deletedAt).toBeNull()
      expect(target.fieldTimestamps).toEqual({})
      expect(target.tags).toEqual({})
    } else {
      const e = s.esperado
      expect(target.createdAt).toBe(iso(e.criadaEm))
      expect(target.deletedAt).toBe(e.excluidaEm ? iso(e.excluidaEm) : null)
      expect(Object.keys(target.fieldTimestamps).sort()).toEqual(Object.keys(e.campos).sort())
      for (const [name, f] of Object.entries(e.campos)) {
        expect(target.fieldTimestamps[name]).toBe(iso(f.ts))
        expect((target as unknown as Record<string, unknown>)[name]).toBe(f.valor)
      }
      expect(target.tags).toEqual(
        Object.fromEntries(Object.entries(e.tags).map(([n, t]) => [n, { present: t.presente, ts: iso(t.ts) }])),
      )
    }
    expect(result).toEqual({ changed: s.mudou, contentChanged: s.conteudoMudou })
  })
})
