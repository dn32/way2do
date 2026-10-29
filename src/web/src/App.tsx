import { useLiveQuery } from 'dexie-react-hooks'
import { useEffect } from 'react'
import { currentSession, endSession, expireSession, retryPendingLogout, startSession } from './data/session'
import { LoginScreen } from './ui/LoginScreen'
import { TasksScreen } from './ui/TasksScreen'
import { UpdateNotice } from './ui/UpdateNotice'

export default function App() {
  // Sessão lida do IndexedDB e observada: login ou logout em outra aba atualiza esta também (nenhuma aba fica com
  // um usuário diferente do cookie). undefined = ainda lendo; null = ninguém logado neste aparelho.
  const user = useLiveQuery(() => currentSession(), [])

  // Logout feito sem conexão: reenvia ao abrir o app e quando a conexão voltar.
  useEffect(() => {
    void retryPendingLogout()
    const onOnline = () => void retryPendingLogout()
    window.addEventListener('online', onOnline)
    return () => window.removeEventListener('online', onOnline)
  }, [])

  if (user === undefined) return null
  return (
    <>
      {user === null ? (
        <LoginScreen onLoggedIn={startSession} />
      ) : (
        <TasksScreen key={user.id} user={user} onLogout={endSession} onSessionExpired={expireSession} />
      )}
      <UpdateNotice />
    </>
  )
}
