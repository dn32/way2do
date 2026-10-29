import { liveQuery } from 'dexie'
import { OfflineError } from '../api/auth'
import { OtherUserError, pull, push, SessionExpiredError, type PullPage, type PushResult } from '../api/sync'
import type { PendingOp, UserDb } from '../data/db'
import { currentSession } from '../data/session'
import { applyPulledPage, applyPushResults, nextBatch, readCursor, writeStatus } from './store'

// Motor de sincronização (escopo, seção 5): push da fila em lotes, pull desde o cursor com merge por campo. Roda em
// uma aba por vez (Web Locks); as outras só leem o IndexedDB e mostram o estado gravado lá.

export const PULL_PAGE = 500
export const DEBOUNCE_MS = 2000
export const PERIODIC_MS = 30_000
export const BACKOFF_MIN_MS = 2000
export const BACKOFF_MAX_MS = 60_000

export interface SyncApi {
  push(userId: string, ops: PendingOp[], signal?: AbortSignal): Promise<PushResult[]>
  pull(userId: string, since: number, limit: number, signal?: AbortSignal): Promise<PullPage>
}

export type CycleResult = 'synced' | 'offline' | 'error' | 'expired' | 'aborted'

/**
 * Um ciclo completo: envia a fila inteira (lote a lote) e depois traz tudo o que mudou desde o cursor. O estado
 * "Sincronizando" só aparece quando há o que enviar ou na primeira sincronização, para o pull periódico não piscar
 * o indicador a cada 30 s.
 */
export async function runCycle(userId: string, db: UserDb, signal: AbortSignal, api: SyncApi = { push, pull }): Promise<CycleResult> {
  try {
    // O banco aberto é do usuário logado? (outra aba pode ter trocado de conta; o servidor confere de novo.)
    if ((await currentSession())?.id !== userId) return 'aborted'

    const cursor = await readCursor(db)
    if (cursor === undefined || (await db.pendingOps.count()) > 0) await writeStatus(db, 'syncing')

    for (;;) {
      const batch = await nextBatch(db)
      if (batch.length === 0) break
      const results = await api.push(userId, batch, signal)
      if (signal.aborted) return 'aborted'
      // Resposta que não tira nada da fila repetiria o mesmo lote para sempre.
      if ((await applyPushResults(db, batch, results)) === 0) throw new Error('push sem resultado para o lote')
    }

    let since = cursor ?? 0
    for (;;) {
      const page = await api.pull(userId, since, PULL_PAGE, signal)
      if (signal.aborted) return 'aborted'
      await applyPulledPage(db, page)
      if (!page.hasMore || page.cursor <= since) break
      since = page.cursor
    }

    await writeStatus(db, 'synced')
    return 'synced'
  } catch (e) {
    if (signal.aborted) return 'aborted'
    const result: CycleResult = e instanceof OfflineError ? 'offline'
      : e instanceof SessionExpiredError || e instanceof OtherUserError ? 'expired'
      : 'error'
    await writeStatus(db, result)
    return result
  }
}

/** Espera depois da n-ésima falha seguida: dobra a cada vez até o teto, com jitter (entre a metade e o total). */
export function backoffDelay(failures: number, random: () => number): number {
  const base = Math.min(BACKOFF_MAX_MS, BACKOFF_MIN_MS * 2 ** Math.max(0, failures - 1))
  return Math.round(base / 2 + random() * (base / 2))
}

export interface EngineDeps {
  api?: SyncApi
  /** Aleatório para o jitter do backoff (injetado nos testes). */
  random?: () => number
}

/**
 * Mantém a sincronização do usuário enquanto `signal` não for abortado. Gatilhos: abertura, evento `online`, alguns
 * segundos depois de cada alteração local, volta ao primeiro plano e a cada 30 s. Em falha, backoff exponencial com
 * jitter; sessão expirada pausa até um novo login.
 */
export function startSync(userId: string, db: UserDb, signal: AbortSignal, deps: EngineDeps = {}): void {
  const run = () => runEngine(userId, db, signal, deps)
  const locks = typeof navigator !== 'undefined' ? navigator.locks : undefined
  if (!locks) {
    void run()
    return
  }
  // Uma aba por usuário segura o lock; se ela fechar, a próxima da fila assume.
  locks.request(`way2do-sync-${userId}`, { signal }, run).catch(() => {
    // abortado enquanto esperava o lock: a tela foi fechada
  })
}

async function runEngine(userId: string, db: UserDb, signal: AbortSignal, deps: EngineDeps): Promise<void> {
  if (signal.aborted) return // fechada antes de pegar o lock: solta na hora
  const random = deps.random ?? Math.random
  let timer: ReturnType<typeof setTimeout> | undefined
  let dueAt = Infinity
  let running = false
  let again = false
  let paused = false
  let failures = 0

  const backoff = () => backoffDelay(failures, random)

  // Agenda o próximo ciclo; um pedido mais cedo substitui o agendado.
  const schedule = (delay: number) => {
    if (signal.aborted || paused) return
    const at = Date.now() + delay
    if (at >= dueAt) return
    clearTimeout(timer)
    dueAt = at
    timer = setTimeout(() => {
      dueAt = Infinity
      void cycle()
    }, delay)
  }

  const cycle = async () => {
    if (running) {
      again = true
      return
    }
    running = true
    again = false
    const result = await runCycle(userId, db, signal, deps.api)
    running = false
    if (signal.aborted || result === 'aborted') return
    if (result === 'expired') {
      paused = true // só um novo login retoma (a tela é remontada)
      return
    }
    if (result === 'synced') {
      failures = 0
      schedule(again ? 0 : PERIODIC_MS)
    } else {
      failures++
      schedule(backoff())
    }
  }

  const now = () => {
    failures = 0
    schedule(0)
  }
  const onVisible = () => {
    if (document.visibilityState === 'visible') schedule(0)
  }
  // Alteração local (desta ou de outra aba): envia depois de alguns segundos, sem atropelar um backoff em curso. A
  // posição da última operação só cresce; a contagem não serve (o envio de um lote pode tirar mais do que entrou).
  let lastSeq: number | undefined
  const pending = liveQuery(async () => (await db.pendingOps.orderBy('seq').last())?.seq ?? 0).subscribe((seq) => {
    if (lastSeq !== undefined && seq > lastSeq) schedule(failures > 0 ? backoff() : DEBOUNCE_MS)
    lastSeq = seq
  })

  if (typeof window !== 'undefined') window.addEventListener('online', now)
  if (typeof document !== 'undefined') document.addEventListener('visibilitychange', onVisible)
  schedule(0)

  await new Promise<void>((resolve) => {
    if (signal.aborted) resolve()
    else signal.addEventListener('abort', () => resolve(), { once: true })
  })
  clearTimeout(timer)
  pending.unsubscribe()
  if (typeof window !== 'undefined') window.removeEventListener('online', now)
  if (typeof document !== 'undefined') document.removeEventListener('visibilitychange', onVisible)
}
