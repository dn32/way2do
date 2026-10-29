import type { PullPage, PushResult } from '../api/sync'
import type { PendingOp, UserDb } from '../data/db'
import { applyChange, InvalidChangeError } from '../domain/merge'
import type { Task } from '../domain/types'
import { asChange } from './serverTask'

// O que o motor de sincronização grava no banco local do usuário. Cada passo é uma transação: um ciclo interrompido
// (aba fechada, rede caiu) nunca deixa a fila, as tarefas e o cursor desencontrados.

export const MAX_OPS_PER_PUSH = 500
// Abaixo do limite de 1 MB do servidor, com folga para o envelope do JSON.
export const MAX_PUSH_BYTES = 900_000

/** Estado do último ciclo, compartilhado entre as abas pelo IndexedDB (só uma aba sincroniza; todas mostram). */
export type SyncState = 'syncing' | 'synced' | 'offline' | 'error' | 'expired'

export interface SyncStatus {
  state: SyncState
  at: string
}

/** Operação recusada pelo servidor, para o aviso na tela. */
export interface Rejection {
  opId: string
  title: string
  reason: string
  /** true: o servidor não tinha a tarefa e ela foi apagada do aparelho; false: voltou para a versão do servidor. */
  discarded: boolean
}

const MAX_REJECTIONS_KEPT = 20

// Os horários do servidor já foram limitados ao relógio dele: ao aplicar o estado do servidor, o relógio do aparelho
// (que pode estar atrasado) não deve cortá-los, senão as cópias divergiriam.
const NO_CLAMP = '9999-12-31T23:59:59.999Z'

export async function readCursor(db: UserDb): Promise<number | undefined> {
  return (await db.meta.get('cursor'))?.value as number | undefined
}

export async function readStatus(db: UserDb): Promise<SyncStatus | undefined> {
  return (await db.meta.get('syncStatus'))?.value as SyncStatus | undefined
}

export async function writeStatus(db: UserDb, state: SyncState, now = new Date().toISOString()): Promise<void> {
  await db.meta.put({ key: 'syncStatus', value: { state, at: now } satisfies SyncStatus })
}

export async function readRejections(db: UserDb): Promise<Rejection[]> {
  return ((await db.meta.get('rejections'))?.value as Rejection[] | undefined) ?? []
}

export async function clearRejections(db: UserDb): Promise<void> {
  await db.meta.delete('rejections')
}

/** Próximo lote da fila, em ordem: até 500 operações e abaixo do limite de tamanho do servidor. */
export async function nextBatch(db: UserDb): Promise<PendingOp[]> {
  const ops = await db.pendingOps.orderBy('seq').limit(MAX_OPS_PER_PUSH).toArray()
  const batch: PendingOp[] = []
  let bytes = 0
  const encoder = new TextEncoder()
  for (const op of ops) {
    const size = encoder.encode(JSON.stringify(op)).length + 1
    if (batch.length > 0 && bytes + size > MAX_PUSH_BYTES) break
    batch.push(op)
    bytes += size
  }
  return batch
}

/**
 * Aplica a resposta do push. Aceitas saem da fila. Recusadas também saem e a cópia local é **substituída** pelo estado
 * do servidor (sem merge, para não manter o valor inválido) ou apagada se o servidor não tem a tarefa; edições feitas
 * depois do envio (ainda na fila) são reaplicadas por cima. Devolve quantas operações saíram da fila.
 */
export async function applyPushResults(db: UserDb, sent: PendingOp[], results: PushResult[], now = new Date().toISOString()): Promise<number> {
  const byOpId = new Map(sent.map((op) => [op.opId, op]))
  return db.transaction('rw', db.tasks, db.pendingOps, db.meta, async () => {
    const removed: number[] = []
    const rejectedTasks = new Map<string, Task | null>()
    const rejections: Rejection[] = []

    for (const result of results) {
      const op = byOpId.get(result.opId)
      if (!op || op.taskId !== result.taskId) continue
      removed.push(op.seq!)
      byOpId.delete(result.opId)
      if (result.status === 'rejected') {
        rejectedTasks.set(op.taskId, result.task)
        const local = await db.tasks.get(op.taskId)
        rejections.push({
          opId: op.opId,
          title: result.task?.title || local?.title || 'Tarefa sem título',
          reason: result.reason ?? 'alteração inválida',
          discarded: result.task === null,
        })
      }
    }
    await db.pendingOps.bulkDelete(removed)

    for (const [taskId, server] of rejectedTasks) {
      const later = await db.pendingOps.where('taskId').equals(taskId).sortBy('seq')
      if (server === null) {
        // O servidor não tem a tarefa (criação recusada): some daqui também, com o que ainda estava na fila dela.
        await db.tasks.delete(taskId)
        await db.pendingOps.bulkDelete(later.map((op) => op.seq!))
        continue
      }
      await db.tasks.put(withQueuedEdits(server, later, now))
    }

    if (rejections.length > 0) {
      const kept = [...(await readRejections(db)), ...rejections].slice(-MAX_REJECTIONS_KEPT)
      await db.meta.put({ key: 'rejections', value: kept })
    }
    return removed.length
  })
}

/**
 * Mescla uma página do pull com as cópias locais pelo mesmo merge por campo do servidor (uma edição feita durante o
 * ciclo não é sobrescrita) e avança o cursor, que só cresce.
 */
export async function applyPulledPage(db: UserDb, page: PullPage, now = new Date().toISOString()): Promise<void> {
  await db.transaction('rw', db.tasks, db.pendingOps, db.meta, async () => {
    const locals = await db.tasks.bulkGet(page.tasks.map((t) => t.id))
    for (const [i, server] of page.tasks.entries()) {
      const local = locals[i]
      if (!local) {
        // Excluída antes de este aparelho conhecê-la: nada a mostrar.
        if (server.deletedAt === null) await db.tasks.put(server)
        continue
      }
      let merged = structuredClone(local)
      try {
        applyChange(merged, asChange(server), NO_CLAMP)
      } catch (e) {
        if (!(e instanceof InvalidChangeError)) throw e
        // A soma das duas cópias quebra um limite (ex.: tags adicionadas em dois aparelhos passam de 20): vale o
        // servidor, com as edições locais ainda na fila por cima; a que estoura o limite será recusada no envio.
        const queued = await db.pendingOps.where('taskId').equals(server.id).sortBy('seq')
        merged = withQueuedEdits(server, queued, now)
      }
      merged.version = Math.max(local.version, server.version)
      merged.changeSeq = Math.max(local.changeSeq, server.changeSeq)
      await db.tasks.put(merged)
    }
    const cursor = (await readCursor(db)) ?? 0
    await db.meta.put({ key: 'cursor', value: Math.max(cursor, page.cursor) })
  })
}

/** Estado do servidor com as edições locais ainda na fila aplicadas por cima; edição inválida fica de fora. */
function withQueuedEdits(server: Task, queued: PendingOp[], now: string): Task {
  const task = structuredClone(server)
  for (const op of queued) {
    try {
      applyChange(task, { fields: op.fields, tags: op.tags, deletedAt: op.deletedAt }, now)
    } catch (e) {
      if (!(e instanceof InvalidChangeError)) throw e // será recusada pelo servidor no próximo envio, com aviso
    }
  }
  return task
}
