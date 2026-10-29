import { ownTag, visibleTags } from '../domain/merge'
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
