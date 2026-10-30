import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { OfflineError, SessionExpiredError } from '../api/errors'
import { IntegrationError, IntegrationNotFoundError, listIntegrations, registerIntegration, removeIntegration, type Integration } from '../api/integrations'
import { AlertIcon, CheckIcon, ExternalLinkIcon, RefreshIcon, TrashIcon, WifiOffIcon } from './icons'
import { Overlay } from './Overlay'
import { useBrowserOnline } from './useBrowserOnline'

type Status = { kind: 'testing' | 'ok' | 'bad'; text: string }

/**
 * Tela Integrações (escopo, seção 6): URLs que recebem as tarefas deste usuário. É uma ação de servidor, fora da fila
 * offline: sem conexão, mostra o aviso e desabilita as ações.
 */
export function IntegrationsPanel({ desktop, onSessionExpired }: { desktop: boolean; onSessionExpired: () => void }) {
  const online = useBrowserOnline()
  const [items, setItems] = useState<Integration[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [url, setUrl] = useState('')
  const [status, setStatus] = useState<Status | null>(null)
  const [removing, setRemoving] = useState<Integration | null>(null)
  const [removeError, setRemoveError] = useState<string | null>(null)
  const [expired, setExpired] = useState(false)

  // Texto do erro; sessão expirada também mostra o "Entrar de novo", como o indicador do topo.
  const fail = useCallback((e: unknown) => {
    if (e instanceof SessionExpiredError) setExpired(true)
    return messageFor(e)
  }, [])

  const load = useCallback(async () => {
    try {
      setItems(await listIntegrations())
      setLoadError(null)
    } catch (e) {
      setLoadError(fail(e))
    }
  }, [fail])

  useEffect(() => {
    if (online) void load()
  }, [online, load])

  const busy = status?.kind === 'testing'
  const disabled = !online || busy

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (disabled) return
    setStatus({ kind: 'testing', text: 'Testando a entrega… o Way2Do está enviando um aviso de teste para o consumidor.' })
    try {
      const created = await registerIntegration(url.trim())
      setItems((current) => [...(current ?? []), created])
      setUrl('')
      setStatus({ kind: 'ok', text: 'Integração cadastrada: o consumidor confirmou o recebimento e passa a receber as alterações das suas tarefas.' })
    } catch (err) {
      setStatus({ kind: 'bad', text: fail(err) })
    }
  }

  const confirmRemove = async () => {
    if (!removing) return
    try {
      await removeIntegration(removing.id)
      setItems((current) => (current ?? []).filter((i) => i.id !== removing.id))
      setRemoving(null)
    } catch (err) {
      if (err instanceof IntegrationNotFoundError) {
        setItems((current) => (current ?? []).filter((i) => i.id !== removing.id))
        setRemoving(null)
        return
      }
      setRemoveError(fail(err))
    }
  }

  return (
    <div className="integrations">
      {desktop && (
        <div className="page-head">
          <div>
            <h1 className="page-title">Integrações</h1>
            <p className="hint">Sistemas externos que recebem as alterações das suas tarefas.</p>
          </div>
        </div>
      )}

      {expired && (
        <div className="notice notice--danger" role="alert">
          <AlertIcon />
          <div className="notice__text">Sua sessão expirou. Suas tarefas continuam salvas neste aparelho.</div>
          <button className="btn btn--secondary" type="button" onClick={onSessionExpired}>Entrar de novo</button>
        </div>
      )}

      {!online && (
        <div className="notice notice--dark" role="status">
          <WifiOffIcon />
          <div className="notice__text">Integrações precisam de internet. Conecte-se para cadastrar ou remover; suas tarefas continuam funcionando offline.</div>
        </div>
      )}

      <form className={disabled && !busy ? 'panel integ-form is-disabled' : 'panel integ-form'} onSubmit={submit} noValidate>
        <div className="field">
          <label className="label" htmlFor="integ-url">URL de entrada do consumidor</label>
          <input
            id="integ-url"
            className="input input--mono"
            type="url"
            inputMode="url"
            autoComplete="off"
            spellCheck={false}
            placeholder="https://consumidor.exemplo.com/in/…"
            value={url}
            disabled={disabled}
            onChange={(e) => setUrl(e.target.value)}
          />
          <p className="hint">Copie do sistema que vai receber os dados. Ela é secreta: depois de salva, aparece só o começo e o fim.</p>
        </div>
        <div className="integ-form__actions">
          <button className="btn btn--primary" type="submit" disabled={disabled || url.trim() === ''}>
            {busy ? 'Testando…' : 'Testar e salvar'}
          </button>
        </div>
        {status && (
          <div className={`status-line status-line--${status.kind}`} role={status.kind === 'bad' ? 'alert' : 'status'}>
            {status.kind === 'testing' ? <RefreshIcon /> : status.kind === 'ok' ? <CheckIcon /> : <AlertIcon />}
            <span>{status.text}</span>
          </div>
        )}
      </form>

      {loadError && (
        <div className="notice notice--danger" role="alert">
          <AlertIcon />
          <div className="notice__text">{loadError}</div>
        </div>
      )}

      <section className="group" aria-label="Integrações cadastradas">
        <h2 className="group-label">Cadastradas{items ? ` (${items.length})` : ''}</h2>
        {items === null ? (
          !loadError && online && <p className="hint">Carregando…</p>
        ) : items.length === 0 ? (
          <p className="hint">Nenhuma integração ainda. Cadastre a URL de entrada de um consumidor acima.</p>
        ) : (
          <ul className="integ-list">
            {items.map((i) => (
              <li key={i.id} className="integ">
                <div className="integ__info">
                  <span className="integ__url">{i.url}</span>
                  <span className="hint">Desde {new Date(i.createdAt).toLocaleString('pt-BR', { dateStyle: 'short', timeStyle: 'short' })}</span>
                </div>
                <div className="integ__actions">
                  {i.dashboardUrl && (
                    <a className="link" href={i.dashboardUrl} target="_blank" rel="noopener noreferrer">
                      <ExternalLinkIcon />Abrir painel
                    </a>
                  )}
                  <button
                    className="btn btn--secondary"
                    type="button"
                    disabled={!online}
                    onClick={() => {
                      setRemoveError(null)
                      setRemoving(i)
                    }}
                  >
                    <TrashIcon />Remover
                  </button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>

      {removing && (
        <Overlay
          title="Remover esta integração?"
          desktop={desktop}
          small
          onClose={() => setRemoving(null)}
          footer={
            <>
              <button className="btn btn--secondary" type="button" onClick={() => setRemoving(null)}>Cancelar</button>
              <button className="btn btn--danger" type="button" disabled={!online} onClick={() => void confirmRemove()}><TrashIcon />Remover</button>
            </>
          }
        >
          <p>
            <span className="integ__url">{removing.url}</span> deixa de receber as alterações das suas tarefas. O que ainda não foi entregue é
            cancelado.
          </p>
          {removeError && <p className="status-line status-line--bad" role="alert">{removeError}</p>}
        </Overlay>
      )}
    </div>
  )
}

function messageFor(e: unknown): string {
  if (e instanceof OfflineError) return 'Sem conexão com o servidor. Integrações precisam de internet.'
  if (e instanceof SessionExpiredError) return 'Sua sessão expirou. Entre de novo para gerenciar as integrações.'
  if (e instanceof IntegrationError) return e.message
  return 'Não foi possível concluir agora. Tente de novo.'
}
