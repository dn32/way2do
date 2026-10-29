import type { Priority, Task } from './types'
import { isUrgent } from './urgency'

// Ordenação padrão da listagem (escopo, seção 4), idêntica a src/Way2Do.Api/Domain/TodoOrdering.cs: pendentes antes
// de concluídas; dentro de cada grupo, urgentes primeiro; vencimento mais próximo (sem vencimento no fim);
// prioridade alta → baixa; criação mais antiga; id. Casos em tests/fixtures/ordenacao.json.

const PRIORITY_RANK: Record<Priority, number> = { high: 0, medium: 1, low: 2 }

/** Ordena calculando a urgência uma vez por tarefa e a devolve junto (a lista usa o mesmo valor no selo). */
export function sortWithUrgency<T extends Task>(tasks: readonly T[]): { task: T; urgent: boolean }[] {
  return tasks
    .map((task) => ({ task, urgent: isUrgent(task.title, task.description) }))
    .sort((x, y) => compare(x.task, x.urgent, y.task, y.urgent))
}

export function sortTasks<T extends Task>(tasks: readonly T[]): T[] {
  return sortWithUrgency(tasks).map((e) => e.task)
}

function compare(a: Task, aUrgent: boolean, b: Task, bUrgent: boolean): number {
  const byStatus = (a.status === 'pending' ? 0 : 1) - (b.status === 'pending' ? 0 : 1)
  if (byStatus) return byStatus

  if (aUrgent !== bUrgent) return aUrgent ? -1 : 1

  if (a.dueDate !== b.dueDate) {
    if (a.dueDate === null) return 1
    if (b.dueDate === null) return -1
    return a.dueDate < b.dueDate ? -1 : 1 // AAAA-MM-DD: a ordem do texto é a ordem da data
  }

  const byPriority = PRIORITY_RANK[a.priority] - PRIORITY_RANK[b.priority]
  if (byPriority) return byPriority

  const byCreation = Date.parse(a.createdAt ?? '') - Date.parse(b.createdAt ?? '')
  if (byCreation) return byCreation

  // Ordem de código UTF-16 do id em minúsculas, igual ao string.CompareOrdinal do servidor.
  const idA = a.id.toLowerCase()
  const idB = b.id.toLowerCase()
  return idA < idB ? -1 : idA > idB ? 1 : 0
}
