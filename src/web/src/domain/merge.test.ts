import { describe, expect, it } from 'vitest'
import { applyChange, InvalidChangeError, visibleTags } from './merge'
import { emptyTask, type Task, type TodoChange } from './types'

// Equivalentes aos testes de TodoMergeValidationTests.cs: regras que não estão nos casos JSON compartilhados.

const NOW = '2026-10-29T13:00:00.000Z'
const BEFORE = '2026-10-29T12:00:00.000Z'

function existing(): Task {
  const task = emptyTask(crypto.randomUUID())
  task.title = 'Original'
  task.createdAt = '2026-10-28T13:00:00.000Z'
  task.fieldTimestamps.title = '2026-10-28T13:00:00.000Z'
  return task
}

function fieldChange(field: string, value: unknown): TodoChange {
  return { fields: { [field]: { value, ts: BEFORE } } }
}

function rejection(task: Task, change: TodoChange): InvalidChangeError {
  try {
    applyChange(task, change, NOW)
  } catch (e) {
    if (e instanceof InvalidChangeError) return e
    throw e
  }
  throw new Error('a alteração deveria ter sido recusada')
}

describe('validação do merge', () => {
  it.each([
    ['color', 'azul', 'campo desconhecido'],
    ['priority', 'urgentissima', 'low, medium ou high'],
    ['status', 'cancelada', 'pending ou done'],
    ['title', null, 'obrigatório'],
    ['title', '   ', 'obrigatório'],
    ['title', '\uFEFF', 'obrigatório'],
    ['dueDate', '29/10/2026', 'AAAA-MM-DD'],
    ['dueDate', '2026-02-30', 'AAAA-MM-DD'],
    ['title', 42, 'texto ou nulo'],
    ['title', 'abc\u0000def', 'caractere inválido'],
    ['description', 'abc\uD800', 'caractere inválido'],
  ])('recusa %s = %j', (field, value, reason) => {
    const task = existing()
    const before = JSON.stringify(task)

    const error = rejection(task, fieldChange(field, value))

    expect(error.field).toBe(field)
    expect(error.reason).toContain(reason)
    expect(JSON.stringify(task)).toBe(before) // nada aplicado
  })

  it.each([
    ['title', 200, true],
    ['title', 201, false],
    ['description', 4000, true],
    ['description', 4001, false],
  ])('limite de %s com %i caracteres: aceito = %s', (field, length, accepted) => {
    const change = fieldChange(field, 'a'.repeat(length))
    if (accepted) applyChange(existing(), change, NOW)
    else expect(() => applyChange(existing(), change, NOW)).toThrow(InvalidChangeError)
  })

  it('recusa tag acima de 40 caracteres e mais de 20 tags visíveis', () => {
    expect(() => applyChange(existing(), { tags: { ['t'.repeat(41)]: { present: true, ts: NOW } } }, NOW)).toThrow(
      InvalidChangeError,
    )

    const full = existing()
    for (let i = 0; i < 20; i++) full.tags[`tag${i}`] = { present: true, ts: BEFORE }
    expect(() => applyChange(full, { tags: { tag20: { present: true, ts: NOW } } }, NOW)).toThrow(InvalidChangeError)
    expect(Object.keys(full.tags)).toHaveLength(20)
  })

  it('remover uma tag libera espaço para outra na mesma alteração', () => {
    const task = existing()
    for (let i = 0; i < 20; i++) task.tags[`tag${i}`] = { present: true, ts: BEFORE }

    applyChange(task, { tags: { tag0: { present: false, ts: NOW }, nova: { present: true, ts: NOW } } }, NOW)

    expect(visibleTags(task)).toHaveLength(20)
    expect(visibleTags(task)).toContain('nova')
  })

  it('grafias diferentes da mesma tag viram uma só (NFC, espaços, sigma final)', () => {
    const task = existing()

    applyChange(
      task,
      {
        tags: {
          'Ac\u0327a\u0303o': { present: true, ts: BEFORE },
          '\uFEFF ação ': { present: false, ts: '2026-10-29T11:00:00.000Z' },
          'λογος': { present: true, ts: BEFORE },
        },
      },
      NOW,
    )

    expect(Object.keys(task.tags).sort()).toEqual(['ação', 'λογοσ'])
    expect(task.tags['ação'].present).toBe(true)
  })

  it('tags com nomes especiais do JavaScript são tags comuns', () => {
    const task = existing()
    const proto = Object.getPrototypeOf(task.tags)

    applyChange(task, { tags: JSON.parse('{"__proto__":{"present":true,"ts":"2026-10-29T12:00:00.000Z"},"constructor":{"present":true,"ts":"2026-10-29T12:00:00.000Z"}}') }, NOW)

    expect(Object.getPrototypeOf(task.tags)).toBe(proto) // o protótipo não foi trocado
    expect(visibleTags(task)).toEqual(['__proto__', 'constructor'])
  })

  it('timestamps ficam em milissegundos e limitados ao relógio de quem aplica', () => {
    const task = existing()

    applyChange(task, { fields: { title: { value: 'Novo', ts: '2099-01-01T00:00:00Z' } } }, NOW)

    expect(task.fieldTimestamps.title).toBe(NOW)
  })
})
