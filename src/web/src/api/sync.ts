import type { PendingOp } from '../data/db'
import type { Task } from '../domain/types'
import { OfflineError, OtherUserError, ServerError, SessionExpiredError } from './errors'
import { toLocalTask, UnexpectedResponseError } from './serverTask'

export interface PushResult {
  opId: string
  taskId: string
  status: 'accepted' | 'rejected'
  field: string | null
  reason: string | null
  /** Estado da tarefa no servidor (só em recusas); null quando o servidor não a tem. */
  task: Task | null
}

export interface PullPage {
  tasks: Task[]
  cursor: number
  hasMore: boolean
}

const TIMEOUT_MS = 15000
// Confere no servidor que o cookie é do dono do banco local (src/Way2Do.Api/Sync/SyncEndpoints.cs).
const EXPECTED_USER_HEADER = 'X-Way2Do-User'

export async function push(userId: string, ops: PendingOp[], signal?: AbortSignal): Promise<PushResult[]> {
  const operations = ops.map(({ opId, taskId, createdAt, fields, tags, deletedAt }) => ({ opId, taskId, createdAt, fields, tags, deletedAt }))
  const body = await request(userId, '/api/sync/push', { method: 'POST', body: JSON.stringify({ operations }) }, signal)
  const results = (body as { results?: unknown })?.results
  if (!Array.isArray(results)) throw new ServerError('resposta do push sem resultados')
  return results.map(parseResult)
}

export async function pull(userId: string, since: number, limit: number, signal?: AbortSignal): Promise<PullPage> {
  const body = (await request(userId, `/api/sync/pull?since=${since}&limit=${limit}`, { method: 'GET' }, signal)) as Record<string, unknown> | null
  if (!body || !Array.isArray(body.tasks) || typeof body.cursor !== 'number' || typeof body.hasMore !== 'boolean')
    throw new ServerError('resposta do pull fora do contrato')
  return { tasks: body.tasks.map(guard(toLocalTask)), cursor: body.cursor, hasMore: body.hasMore }
}

async function request(userId: string, path: string, init: RequestInit, signal?: AbortSignal): Promise<unknown> {
  // O prazo vale até o corpo ser lido, não só até os cabeçalhos chegarem.
  const { signal: combined, done } = withTimeout(signal, TIMEOUT_MS)
  try {
    let response: Response
    try {
      response = await fetch(path, {
        ...init,
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', [EXPECTED_USER_HEADER]: userId },
        signal: combined,
      })
    } catch (e) {
      if (signal?.aborted) throw e // a tela foi fechada: não é falta de rede
      throw new OfflineError()
    }
    if (response.status === 401) throw new SessionExpiredError()
    if (response.status === 409) throw new OtherUserError()
    if (!response.ok) throw new ServerError(`HTTP ${response.status}`)
    try {
      return await response.json()
    } catch (e) {
      if (signal?.aborted) throw e
      throw new ServerError('resposta não é JSON ou chegou incompleta')
    }
  } finally {
    done()
  }
}

/**
 * Sinal que aborta com o da tela ou depois de `ms`. Montado à mão: `AbortSignal.any` só existe a partir do iOS 17.4,
 * e lá o TypeError viraria "Offline" para sempre.
 */
function withTimeout(signal: AbortSignal | undefined, ms: number): { signal: AbortSignal; done: () => void } {
  const controller = new AbortController()
  const abort = () => controller.abort()
  const timer = setTimeout(abort, ms)
  if (signal?.aborted) abort()
  else signal?.addEventListener('abort', abort, { once: true })
  return {
    signal: controller.signal,
    done: () => {
      clearTimeout(timer)
      signal?.removeEventListener('abort', abort)
    },
  }
}

function parseResult(raw: unknown): PushResult {
  const r = (raw ?? {}) as Record<string, unknown>
  if (typeof r.opId !== 'string' || typeof r.taskId !== 'string' || (r.status !== 'accepted' && r.status !== 'rejected'))
    throw new ServerError('resultado do push fora do contrato')
  return {
    opId: r.opId.toLowerCase(),
    taskId: r.taskId.toLowerCase(),
    status: r.status,
    field: typeof r.field === 'string' ? r.field : null,
    reason: typeof r.reason === 'string' ? r.reason : null,
    task: r.task === null || r.task === undefined ? null : guard(toLocalTask)(r.task),
  }
}

function guard<T>(parse: (raw: unknown) => T): (raw: unknown) => T {
  return (raw) => {
    try {
      return parse(raw)
    } catch (e) {
      if (e instanceof UnexpectedResponseError) throw new ServerError(e.message)
      throw e
    }
  }
}
