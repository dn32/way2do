import { logout as logoutRequest, type User } from '../api/auth'
import { META_KEYS, sessionDb, userDb } from './db'

export async function currentSession(): Promise<User | null> {
  const session = await sessionDb.session.get('current')
  return session ? { id: session.userId, name: session.name } : null
}

/**
 * Login bem-sucedido: o servidor já trocou o cookie, então um logout pendente de antes não deve mais ser enviado, e
 * o estado "Sessão expirada" gravado no banco do usuário deixa de valer (o próximo ciclo grava o atual).
 */
export async function startSession(user: User): Promise<void> {
  await userDb(user.id).meta.delete(META_KEYS.syncStatus)
  await sessionDb.transaction('rw', sessionDb.session, sessionDb.flags, async () => {
    await sessionDb.session.put({ key: 'current', userId: user.id, name: user.name })
    await sessionDb.flags.delete('pendingLogout')
  })
}

/**
 * Sai: o aparelho esquece o usuário na hora (a tela troca sem esperar a rede) e o banco local dele NÃO é apagado
 * (as alterações pendentes vão no próximo login). Se o servidor não confirmar o logout (sem conexão, timeout, erro),
 * fica uma marca no aparelho e o pedido é reenviado quando a conexão voltar, para o cookie não continuar válido.
 */
export async function endSession(): Promise<void> {
  await sessionDb.transaction('rw', sessionDb.session, sessionDb.flags, async () => {
    await sessionDb.session.delete('current')
    await sessionDb.flags.put({ key: 'pendingLogout', value: true })
  })
  await retryPendingLogout()
}

/** Reenvia o logout pendente, se houver e se ninguém tiver entrado desde então. */
export async function retryPendingLogout(): Promise<void> {
  if (!(await sessionDb.flags.get('pendingLogout'))) return
  if (await currentSession()) {
    await sessionDb.flags.delete('pendingLogout') // alguém entrou: o cookie já é outro
    return
  }
  if (await logoutRequest()) await sessionDb.flags.delete('pendingLogout')
}

/**
 * Sessão expirada (o servidor respondeu 401): volta para o login sem pedir logout, porque o cookie já não vale. O
 * banco local do usuário fica intacto e a fila é enviada depois do novo login.
 */
export async function expireSession(): Promise<void> {
  await sessionDb.session.delete('current')
}
