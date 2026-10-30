import { describe, expect, it } from 'vitest'
import { emptyTask, type Task } from '../domain/types'
import { diffEdit, type FormValues } from './localTasks'

const ts = '2026-10-29T10:00:00.000Z'
const snapshot: Task = {
  ...emptyTask('3f0c7c1e-6a3b-4d6f-9b1a-2a7d5e8c9f01'),
  title: 'Trocar disjuntor',
  description: 'Galpão 3',
  dueDate: '2026-11-02',
  priority: 'high',
  tags: { obra: { present: true, ts }, antiga: { present: false, ts } },
  createdAt: ts,
}
const unchanged: FormValues = { title: 'Trocar disjuntor', description: 'Galpão 3', dueDate: '2026-11-02', priority: 'high', tags: ['obra'] }

// O merge por campo depende disto: só vai o que mudou, para um campo não tocado não vencer a edição de outro aparelho.
describe('diffEdit: só o que mudou', () => {
  it.each<[string, Partial<FormValues>, ReturnType<typeof diffEdit>]>([
    ['nada mudou: nada vai', {}, { fields: {}, tags: {} }],
    ['só o título', { title: 'Trocar disjuntor já' }, { fields: { title: 'Trocar disjuntor já' }, tags: {} }],
    ['descrição apagada vira null', { description: '' }, { fields: { description: null }, tags: {} }],
    ['descrição só com espaços vira null', { description: '   ' }, { fields: { description: null }, tags: {} }],
    ['vencimento removido vira null', { dueDate: '' }, { fields: { dueDate: null }, tags: {} }],
    ['só a prioridade', { priority: 'low' }, { fields: { priority: 'low' }, tags: {} }],
    ['tag nova e tag removida', { tags: ['cliente'] }, { fields: {}, tags: { cliente: true, obra: false } }],
    ['tag que já estava removida e volta', { tags: ['obra', 'antiga'] }, { fields: {}, tags: { antiga: true } }],
  ])('%s', (_caso, change, expected) => {
    expect(diffEdit(snapshot, { ...unchanged, ...change })).toEqual(expected)
  })

  it('tarefa nova: título e prioridade sempre; descrição e vencimento só se preenchidos', () => {
    expect(diffEdit(null, { title: 'Nova', description: '  ', dueDate: '', priority: 'medium', tags: [] }))
      .toEqual({ fields: { title: 'Nova', priority: 'medium' }, tags: {} })
    expect(diffEdit(null, { title: 'Nova', description: 'Detalhe', dueDate: '2026-11-02', priority: 'low', tags: ['obra'] }))
      .toEqual({ fields: { title: 'Nova', description: 'Detalhe', dueDate: '2026-11-02', priority: 'low' }, tags: { obra: true } })
  })
})
