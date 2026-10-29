// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { emptyTask, type Task } from '../domain/types'
import type { LocalEdit } from '../data/localTasks'
import { TaskForm } from './TaskForm'

afterEach(cleanup)

function task(overrides: Partial<Task>): Task {
  return { ...emptyTask('3f0c7c1e-6a3b-4d6f-9b1a-2a7d5e8c9f01'), title: 'A', createdAt: '2026-10-29T10:00:00.000Z', ...overrides }
}

function renderForm(t: Task, onSave: (edit: LocalEdit) => Promise<void>, removed = false) {
  const props = { knownTags: [], desktop: true, onSave, onCancel: () => {}, removed }
  const view = render(<TaskForm task={t} {...props} />)
  return { ...view, rerenderWith: (next: Task | null, gone = false) => view.rerender(<TaskForm task={next} {...props} removed={gone} />) }
}

describe('formulário de tarefa', () => {
  it('envia só o que o usuário mudou, mesmo que outro aparelho altere a tarefa com o formulário aberto', async () => {
    const onSave = vi.fn(async () => {})
    const ts = '2026-10-29T10:00:00.000Z'
    const { rerenderWith } = renderForm(task({ title: 'A', tags: { obra: { present: true, ts } } }), onSave)

    // Outro aparelho troca o título e remove a tag enquanto o formulário está aberto.
    rerenderWith(task({ title: 'B', tags: { obra: { present: false, ts } } }))
    fireEvent.click(screen.getByRole('button', { name: 'Alta' }))
    fireEvent.click(screen.getByRole('button', { name: 'Salvar tarefa' }))

    await vi.waitFor(() => expect(onSave).toHaveBeenCalled())
    expect(onSave).toHaveBeenCalledWith({ fields: { priority: 'high' }, tags: {} })
  })

  it('avisa e não salva quando a tarefa foi excluída em outro aparelho', () => {
    const onSave = vi.fn(async () => {})
    const { rerenderWith } = renderForm(task({}), onSave)

    rerenderWith(null, true)

    expect(screen.getByRole('alert').textContent).toContain('excluída em outro aparelho')
    expect((screen.getByRole('button', { name: 'Salvar tarefa' }) as HTMLButtonElement).disabled).toBe(true)
  })

  it('mostra erro do banco local em vez de falhar em silêncio', async () => {
    const onSave = vi.fn(async () => {
      throw new DOMException('cota cheia', 'QuotaExceededError')
    })
    renderForm(task({}), onSave)

    fireEvent.click(screen.getByRole('button', { name: 'Alta' }))
    fireEvent.click(screen.getByRole('button', { name: 'Salvar tarefa' }))

    expect((await screen.findByRole('alert')).textContent).toContain('Não foi possível salvar neste aparelho')
  })
})
