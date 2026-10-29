import { useRegisterSW } from 'virtual:pwa-register/react'
import { RefreshIcon } from './icons'

/**
 * "Nova versão disponível": o Service Worker novo fica esperando até o usuário pedir, para a página não recarregar
 * no meio de uma edição (escopo, seção 10).
 */
export function UpdateNotice() {
  const {
    needRefresh: [needRefresh],
    updateServiceWorker,
  } = useRegisterSW()
  if (!needRefresh) return null
  return (
    <div className="update-toast toast" role="status">
      <div className="notice notice--info">
        <RefreshIcon />
        <div className="notice__text">
          <div className="notice__title">Nova versão disponível</div>
          <div className="notice__actions">
            <button className="btn btn--primary" type="button" onClick={() => void updateServiceWorker(true)}>Atualizar</button>
          </div>
        </div>
      </div>
    </div>
  )
}
