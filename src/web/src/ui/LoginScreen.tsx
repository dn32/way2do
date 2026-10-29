import { useState, type FormEvent } from 'react'
import { InvalidCredentialsError, login, OfflineError, type User } from '../api/auth'
import { CheckIcon, InfoIcon } from './icons'

export function LoginScreen({ onLoggedIn }: { onLoggedIn: (user: User) => Promise<void> }) {
  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<{ kind: 'offline' | 'credentials' | 'other'; message: string } | null>(null)
  const [sending, setSending] = useState(false)

  async function submit(e: FormEvent) {
    e.preventDefault()
    setSending(true)
    setError(null)
    try {
      await onLoggedIn(await login(userName, password))
    } catch (err) {
      if (err instanceof OfflineError) setError({ kind: 'offline', message: 'Sem conexão. O login precisa de internet; depois de entrar, o app funciona mesmo sem sinal.' })
      else if (err instanceof InvalidCredentialsError) setError({ kind: 'credentials', message: 'Usuário ou senha inválidos.' })
      else setError({ kind: 'other', message: err instanceof Error ? err.message : 'Não foi possível entrar.' })
    } finally {
      setSending(false)
    }
  }

  return (
    <div className="w2 login">
      <form className="login__card" onSubmit={submit}>
        <div className="login__head">
          <div className="brand brand--login">
            <span className="brand__mark"><CheckIcon /></span>
            Way2Do
          </div>
          <h1 className="login__title">Entrar</h1>
          <p className="login__lead">Use o usuário e a senha que você recebeu.</p>
        </div>

        {error?.kind === 'offline' && (
          <div className="notice notice--warn" role="alert">
            <InfoIcon />
            <div>{error.message}</div>
          </div>
        )}

        <div className="field">
          <label className="label" htmlFor="login-user">Usuário</label>
          <input className="input" id="login-user" type="text" autoComplete="username" autoCapitalize="none" value={userName} onChange={(e) => setUserName(e.target.value)} required />
        </div>
        <div className="field">
          <label className="label" htmlFor="login-password">Senha</label>
          <input
            className={error?.kind === 'credentials' ? 'input input--error' : 'input'}
            id="login-password"
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
          />
          {error && error.kind !== 'offline' && (
            <span className="error-msg" role="alert">
              <InfoIcon size="sm" />
              {error.message}
            </span>
          )}
        </div>

        <p className="hint login__hint">
          <InfoIcon size="sm" />
          O login precisa de internet. Depois de entrar, o app funciona mesmo sem sinal.
        </p>
        <button className="btn btn--primary btn--block btn--lg" type="submit" disabled={sending}>
          {sending ? 'Entrando…' : 'Entrar'}
        </button>
      </form>
    </div>
  )
}
