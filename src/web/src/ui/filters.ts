import { ownTag, visibleTags } from '../domain/merge'
import { sortWithUrgency } from '../domain/ordering'
import { normalize } from '../domain/text'
import type { Priority, Task } from '../domain/types'

export interface Filters {
  status: 'all' | 'pending' | 'done'
  priority: 'all' | Priority
  tag: string | null
  query: string
}

export const NO_FILTERS: Filters = { status: 'all', priority: 'all', tag: null, query: '' }

/** Quantos filtros estão ativos (o contador do botão Filtros). */
export function activeFilterCount(f: Filters): number {
  return (f.status !== 'all' ? 1 : 0) + (f.priority !== 'all' ? 1 : 0) + (f.tag ? 1 : 0) + (f.query.trim() ? 1 : 0)
}

/** Aplica filtros e busca. A busca ignora maiúsculas e acentos (mesma normalização da regra urgente). */
export function filterTasks<T extends Task>(tasks: readonly T[], f: Filters): T[] {
  const query = normalize(f.query.trim())
  return tasks.filter(
    (t) =>
      (f.status === 'all' || t.status === f.status) &&
      (f.priority === 'all' || t.priority === f.priority) &&
      (!f.tag || ownTag(t.tags, f.tag)?.present === true) &&
      (!query || `${normalize(t.title)}\n${normalize(t.description)}`.includes(query)),
  )
}

/** Tags existentes, com a contagem de tarefas que as usam, em ordem de uso. */
export function tagCounts(tasks: readonly Task[]): { name: string; count: number }[] {
  const counts = new Map<string, number>()
  for (const t of tasks) for (const tag of visibleTags(t)) counts.set(tag, (counts.get(tag) ?? 0) + 1)
  return [...counts].map(([name, count]) => ({ name, count })).sort((a, b) => b.count - a.count || (a.name < b.name ? -1 : 1))
}

type Sorted = ReturnType<typeof sortWithUrgency<Task>>

/** O que depende só do banco local: tarefas não excluídas, totais e tags (não mudam com os filtros). */
export interface TaskSummary {
  alive: Task[]
  totals: { pending: number; done: number }
  tags: { name: string; count: number }[]
}

/** O que depende dos filtros: a lista na ordem de exibição (urgentes primeiro dentro de cada grupo). */
export interface TaskArrangement {
  visible: Sorted
  pending: Sorted
  done: Sorted
}

export type TaskGroups = TaskSummary & TaskArrangement

export function summarizeTasks(stored: readonly Task[]): TaskSummary {
  const alive = stored.filter((t) => t.deletedAt === null)
  return {
    alive,
    totals: { pending: alive.filter((t) => t.status === 'pending').length, done: alive.filter((t) => t.status === 'done').length },
    tags: tagCounts(alive),
  }
}

export function arrangeTasks(alive: readonly Task[], filters: Filters): TaskArrangement {
  const visible = sortWithUrgency(filterTasks(alive, filters))
  return { visible, pending: visible.filter((e) => e.task.status === 'pending'), done: visible.filter((e) => e.task.status === 'done') }
}

/** As duas partes juntas (a tela calcula cada uma com o seu memo). */
export function groupTasks(stored: readonly Task[], filters: Filters): TaskGroups {
  const summary = summarizeTasks(stored)
  return { ...summary, ...arrangeTasks(summary.alive, filters) }
}
