import type { SyncStatus } from './store'

export type IndicatorKind = 'offline' | 'pending' | 'syncing' | 'ok' | 'error' | 'expired'

/**
 * Estado mostrado no topo (escopo, seção 5). `navigator.onLine` é só uma dica: "Offline" também vem da falha real
 * da última requisição. Fila com itens fora de um ciclo em andamento é "Pendente".
 */
export function indicatorKind(status: SyncStatus | undefined, pending: number, browserOnline: boolean): IndicatorKind {
  if (status?.state === 'expired') return 'expired'
  if (!browserOnline || status?.state === 'offline') return 'offline'
  if (status === undefined || status.state === 'syncing') return 'syncing'
  if (status.state === 'error') return 'error'
  return pending > 0 ? 'pending' : 'ok'
}

/**
 * Primeira sincronização neste aparelho (nenhum pull completo ainda): a lista mostra "Carregando suas tarefas…" em
 * vez de "Nenhuma tarefa ainda", a menos que a tentativa já tenha falhado (aí a lista abre e o indicador explica).
 */
export function isFirstSync(cursor: number | undefined, indicator: IndicatorKind): boolean {
  return cursor === undefined && (indicator === 'syncing' || indicator === 'ok' || indicator === 'pending')
}
