import type { IndicatorKind } from '../sync/indicator'
import { CheckIcon, ClockIcon, ErrorIcon, LockIcon, RefreshIcon, WifiOffIcon } from './icons'

/** Indicador de sincronização, sempre visível no topo, com palavras do usuário (nunca push, pull ou fila). */
export function SyncBar({ kind, pending, desktop, onReLogin }: { kind: IndicatorKind; pending: number; desktop: boolean; onReLogin: () => void }) {
  const cls = (state: string) => `sync sync--${state}${desktop ? ' sync--desk' : ''}`
  const count = pending === 1 ? '1 pendente' : `${pending} pendentes`
  switch (kind) {
    case 'offline':
      return (
        <div className={cls('offline')} role="status">
          <WifiOffIcon />
          <span className="sync__text">Offline{pending > 0 && <span className="sync__detail"> · {count}</span>}</span>
        </div>
      )
    case 'pending':
      return (
        <div className={cls('pending')} role="status">
          <ClockIcon />
          <span className="sync__text">Pendente <span className="sync__detail">({pending})</span></span>
        </div>
      )
    case 'syncing':
      return (
        <div className={cls('syncing')} role="status">
          <RefreshIcon />
          <span className="sync__text">Sincronizando…</span>
        </div>
      )
    case 'ok':
      return (
        <div className={cls('ok')} role="status">
          <CheckIcon />
          <span className="sync__text">Sincronizado</span>
        </div>
      )
    case 'error':
      return (
        <div className={cls('error')} role="status">
          <ErrorIcon />
          <span className="sync__text">Erro no servidor <span className="sync__detail">· tentando de novo</span></span>
        </div>
      )
    case 'expired':
      return (
        <div className={cls('expired')} role="alert">
          <LockIcon />
          <span className="sync__text">Sessão expirada</span>
          <button className="sync__action" type="button" onClick={onReLogin}>Entrar de novo</button>
        </div>
      )
  }
}
