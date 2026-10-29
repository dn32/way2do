import { useSyncExternalStore } from 'react'
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

function subscribe(onChange: () => void): () => void {
  window.addEventListener('online', onChange)
  window.addEventListener('offline', onChange)
  return () => {
    window.removeEventListener('online', onChange)
    window.removeEventListener('offline', onChange)
  }
}

export function useBrowserOnline(): boolean {
  return useSyncExternalStore(subscribe, () => navigator.onLine, () => true)
}
