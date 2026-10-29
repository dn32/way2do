import type { Priority, Status } from '../domain/types'

export const PRIORITY_LABEL: Record<Priority, string> = { high: 'Alta', medium: 'Média', low: 'Baixa' }
export const PRIORITY_LEVEL: Record<Priority, 1 | 2 | 3> = { high: 3, medium: 2, low: 1 }
export const PRIORITY_BADGE: Record<Priority, string> = {
  high: 'badge badge--prio-high',
  medium: 'badge badge--prio-med',
  low: 'badge badge--prio-low',
}

/** Data de hoje no fuso do aparelho, no formato AAAA-MM-DD ("vencida" é pela data local). */
export function todayLocal(now = new Date()): string {
  const y = now.getFullYear()
  const m = String(now.getMonth() + 1).padStart(2, '0')
  const d = String(now.getDate()).padStart(2, '0')
  return `${y}-${m}-${d}`
}

function dayMonth(date: string): string {
  const [, m, d] = date.split('-')
  return `${d}/${m}`
}

/** Selo de vencimento: vencida e vence hoje em destaque (só para pendentes). */
export function dueBadge(dueDate: string, status: Status, today = todayLocal()): { label: string; className: string } {
  if (status === 'done') return { label: dayMonth(dueDate), className: 'badge badge--due' }
  if (dueDate < today) return { label: `Vencida · ${dayMonth(dueDate)}`, className: 'badge badge--overdue' }
  if (dueDate === today) return { label: 'Vence hoje', className: 'badge badge--today' }
  return { label: `Vence ${dayMonth(dueDate)}`, className: 'badge badge--due' }
}
