import { UUID_PATTERN } from '../data/ids'
import { OfflineError } from './errors'

export interface User {
  id: string
  name: string
}

export class InvalidCredentialsError extends Error {}

const TIMEOUT_MS = 8000

export async function login(userName: string, password: string): Promise<User> {
  let response: Response
  try {
    response = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'same-origin',
      body: JSON.stringify({ userName, password }),
      signal: AbortSignal.timeout(TIMEOUT_MS),
    })
  } catch {
    throw new OfflineError()
  }
  if (response.status === 401) throw new InvalidCredentialsError()
  if (response.status === 429) throw new Error('Muitas tentativas. Aguarde um minuto e tente de novo.')
  if (!response.ok) throw new Error('Não foi possível entrar agora. Tente de novo em instantes.')
  return parseUser(await response.json())
}

/**
 * Pede ao servidor que apague o cookie da sessão. Devolve true só com resposta de sucesso: sem conexão, timeout ou
 * erro (ex.: 502 do proxy com a API fora), o cookie pode continuar válido e o pedido precisa ser repetido.
 */
export async function logout(): Promise<boolean> {
  try {
    const response = await fetch('/api/auth/logout', {
      method: 'POST',
      credentials: 'same-origin',
      signal: AbortSignal.timeout(TIMEOUT_MS),
    })
    return response.ok
  } catch {
    return false
  }
}

// O id vira o nome do banco local do usuário: só aceita um UUID de verdade.
function parseUser(body: unknown): User {
  const { id, name } = (body ?? {}) as Partial<User>
  if (typeof id !== 'string' || !UUID_PATTERN.test(id) || typeof name !== 'string')
    throw new Error('Resposta inesperada do servidor ao entrar.')
  return { id: id.toLowerCase(), name }
}
