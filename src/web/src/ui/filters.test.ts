import { describe, expect, it } from 'vitest'
import { emptyTask, type Task } from '../domain/types'
import { activeFilterCount, filterTasks, NO_FILTERS, tagCounts } from './filters'

function task(title: string, extra: Partial<Task> = {}): Task {
  return { ...emptyTask(crypto.randomUUID()), title, createdAt: '2026-10-29T10:00:00.000Z', ...extra }
}

const ts = '2026-10-29T10:00:00.000Z'
const galpao = task('Trocar disjuntor do galpão 3', { priority: 'high', tags: { obra: { present: true, ts } } })
const laudo = task('Enviar laudo', { description: 'Cliente aguardando', status: 'done', tags: { cliente: { present: true, ts }, obra: { present: true, ts } } })
const epi = task('Comprar EPI', { priority: 'low', tags: { obra: { present: false, ts } } })
const all = [galpao, laudo, epi]

describe('filtros e busca', () => {
  it('busca ignora maiúsculas e acentos, no título e na descrição', () => {
    expect(filterTasks(all, { ...NO_FILTERS, query: 'GALPAO' })).toEqual([galpao])
    expect(filterTasks(all, { ...NO_FILTERS, query: 'aguardando' })).toEqual([laudo])
  })

  it('filtra por status, prioridade e tag visível', () => {
    expect(filterTasks(all, { ...NO_FILTERS, status: 'done' })).toEqual([laudo])
    expect(filterTasks(all, { ...NO_FILTERS, priority: 'low' })).toEqual([epi])
    expect(filterTasks(all, { ...NO_FILTERS, tag: 'obra' })).toEqual([galpao, laudo]) // em "epi" a tag foi removida
  })

  it('conta filtros ativos e tags em uso', () => {
    expect(activeFilterCount({ status: 'pending', priority: 'all', tag: 'obra', query: '  ' })).toBe(2)
    expect(tagCounts(all)).toEqual([
      { name: 'obra', count: 2 },
      { name: 'cliente', count: 1 },
    ])
  })
})
