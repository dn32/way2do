// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { emptyTask, type Task } from '../domain/types'
import { groupTasks, NO_FILTERS } from './filters'
import { TaskListContent } from './TaskList'
import { AccountMenu, DeleteTaskDialog, LogoutDialog } from './TasksDialogs'

afterEach(cleanup)

const noop = () => {}

describe('diálogos da tela de tarefas', () => {
  it.each([
    [1, '1 alteração não sincronizada'],
    [3, '3 alterações não sincronizadas'],
  ])('sair com %i pendência(s) avisa que elas ficam guardadas', (pending, text) => {
    const onConfirm = vi.fn()
    render(<LogoutDialog pending={pending} desktop onCancel={noop} onConfirm={onConfirm} />)

    expect(screen.getByText(text)).toBeTruthy()
    expect(screen.getByText(/serão enviadas no seu próximo login/)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Sair mesmo assim' }))
    expect(onConfirm).toHaveBeenCalledOnce()
  })

  it('excluir mostra o título e só confirma no botão de excluir', () => {
    const onConfirm = vi.fn()
    const onCancel = vi.fn()
    render(<DeleteTaskDialog title="Trocar disjuntor" desktop onCancel={onCancel} onConfirm={onConfirm} />)

    expect(screen.getByText('Trocar disjuntor')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    expect(onConfirm).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Excluir tarefa' }))
    expect(onConfirm).toHaveBeenCalledOnce()
  })

  it('menu da conta marca a aba atual e navega', () => {
    const onNavigate = vi.fn()
    render(<AccountMenu userName="ana" view="tasks" onNavigate={onNavigate} onLogout={noop} onClose={noop} />)

    expect(screen.getByRole('button', { name: 'Tarefas' }).getAttribute('aria-current')).toBe('page')
    fireEvent.click(screen.getByRole('button', { name: 'Integrações' }))
    expect(onNavigate).toHaveBeenCalledWith('integrations')
  })
})

describe('conteúdo da lista', () => {
  const ts = '2026-10-29T10:00:00.000Z'
  const task = (title: string, extra: Partial<Task> = {}): Task => ({ ...emptyTask(crypto.randomUUID()), title, createdAt: ts, ...extra })
  const props = { failure: null, rejections: [], desktop: true, onCloseFailure: noop, onCloseRejections: noop, onToggle: noop, onOpen: noop, onCreate: noop, onClearFilters: noop }

  it.each([
    [true, 'Carregando suas tarefas…'],
    [false, 'Nenhuma tarefa ainda'],
  ])('sem tarefas, primeira sincronização = %s mostra "%s"', (firstSync, text) => {
    render(<TaskListContent {...props} groups={groupTasks([], NO_FILTERS)} firstSync={firstSync} />)
    expect(screen.getByText(text)).toBeTruthy()
  })

  it('filtro sem resultado oferece limpar; com resultado, mostra os grupos com a contagem', () => {
    const onClearFilters = vi.fn()
    const tasks = [task('Urgente: disjuntor'), task('Laudo', { status: 'done' })]
    const { rerender } = render(<TaskListContent {...props} onClearFilters={onClearFilters} groups={groupTasks(tasks, { ...NO_FILTERS, query: 'nada' })} firstSync={false} />)
    fireEvent.click(screen.getByRole('button', { name: 'Limpar filtros' }))
    expect(onClearFilters).toHaveBeenCalledOnce()

    rerender(<TaskListContent {...props} groups={groupTasks(tasks, NO_FILTERS)} firstSync={false} />)
    expect(screen.getByText('Pendentes (1)')).toBeTruthy()
    expect(screen.getByText('Concluídas (1)')).toBeTruthy()
  })
})
