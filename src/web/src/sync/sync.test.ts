import 'fake-indexeddb/auto'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { OfflineError } from '../api/auth'
import { OtherUserError, pull, push, ServerError, SessionExpiredError, type PullPage, type PushResult } from '../api/sync'
import { sessionDb, UserDb, type PendingOp } from '../data/db'
import { saveLocalEdit } from '../data/localTasks'
import { startSession } from '../data/session'
import { applyChange, InvalidChangeError } from '../domain/merge'
import { emptyTask, type Task } from '../domain/types'
import { backoffDelay, BACKOFF_MAX_MS, BACKOFF_MIN_MS, DEBOUNCE_MS, runCycle, startSync, type SyncApi } from './engine'
import { indicatorKind } from './indicator'
import { toLocalTask } from './serverTask'
import { applyPulledPage, applyPushResults, MAX_OPS_PER_PUSH, nextBatch, readCursor, readRejections, readStatus } from './store'

const T1 = '2026-10-28T10:00:00.000Z'
const T2 = '2026-10-28T11:00:00.000Z'
const T3 = '2026-10-28T12:00:00.000Z'

type Mode = 'ok' | 'offline' | 401 | 409 | 500

/** Servidor em memória com o mesmo merge (o real é testado no xUnit): push por operação, pull por change_seq. */
class FakeServer implements SyncApi {
  tasks = new Map<string, Task>()
  seq = 0
  mode: Mode = 'ok'
  pushes = 0
  pulls = 0
  beforePullReturns?: () => Promise<void>

  async push(_userId: string, ops: PendingOp[]): Promise<PushResult[]> {
    this.fail()
    this.pushes++
    const results: PushResult[] = []
    for (const op of ops) {
      const task = structuredClone(this.tasks.get(op.taskId) ?? emptyTask(op.taskId))
      try {
        const r = applyChange(task, { fields: op.fields, tags: op.tags, deletedAt: op.deletedAt, createdAt: op.createdAt }, new Date().toISOString())
        if (r.changed) {
          if (r.contentChanged) task.version++
          task.changeSeq = ++this.seq
          this.tasks.set(task.id, task)
        }
        results.push({ opId: op.opId, taskId: op.taskId, status: 'accepted', field: null, reason: null, task: null })
      } catch (e) {
        if (!(e instanceof InvalidChangeError)) throw e
        results.push({ opId: op.opId, taskId: op.taskId, status: 'rejected', field: e.field, reason: e.reason, task: null })
      }
    }
    // Como o servidor real: a recusa traz o estado da tarefa ao fim do lote (null se ele não a tem).
    return results.map((r) => (r.status === 'rejected' ? { ...r, task: structuredClone(this.tasks.get(r.taskId) ?? null) } : r))
  }

  async pull(_userId: string, since: number, limit: number): Promise<PullPage> {
    this.fail()
    this.pulls++
    const all = [...this.tasks.values()].filter((t) => t.changeSeq > since).sort((a, b) => a.changeSeq - b.changeSeq)
    const page = all.slice(0, limit)
    await this.beforePullReturns?.()
    return { tasks: structuredClone(page), cursor: page.at(-1)?.changeSeq ?? since, hasMore: all.length > limit }
  }

  private fail() {
    if (this.mode === 'offline') throw new OfflineError()
    if (this.mode === 401) throw new SessionExpiredError()
    if (this.mode === 409) throw new OtherUserError()
    if (this.mode === 500) throw new ServerError('HTTP 500')
  }
}

const never = new AbortController().signal

async function device(userId: string) {
  const db = new UserDb(`${userId}-${crypto.randomUUID()}`) // dois "aparelhos" do mesmo usuário
  return db
}

function serverTask(id: string, overrides: Partial<Task> = {}): Task {
  return {
    ...emptyTask(id),
    title: 'do servidor',
    fieldTimestamps: { title: T1 },
    createdAt: T1,
    version: 1,
    changeSeq: 1,
    ...overrides,
  }
}

describe('sincronização', () => {
  const userId = crypto.randomUUID()
  const dbs: UserDb[] = []
  let server: FakeServer

  const open = async () => {
    const db = await device(userId)
    dbs.push(db)
    return db
  }

  beforeEach(async () => {
    server = new FakeServer()
    await startSession({ id: userId, name: 'ana' })
  })

  afterEach(async () => {
    while (dbs.length) await dbs.pop()!.delete()
    await sessionDb.session.clear()
    vi.unstubAllGlobals()
  })

  describe('ciclo', () => {
    it('edições offline sobem quando a conexão volta, e a fila esvazia', async () => {
      const db = await open()
      const id = crypto.randomUUID()
      await saveLocalEdit(db, id, { fields: { title: 'Trocar disjuntor' } }, T1)
      await saveLocalEdit(db, id, { fields: { status: 'done' } }, T2)

      server.mode = 'offline'
      expect(await runCycle(userId, db, never, server)).toBe('offline')
      expect(await db.pendingOps.count()).toBe(2)
      expect((await readStatus(db))?.state).toBe('offline')

      server.mode = 'ok'
      expect(await runCycle(userId, db, never, server)).toBe('synced')
      expect(await db.pendingOps.count()).toBe(0)
      expect(server.tasks.get(id)).toMatchObject({ title: 'Trocar disjuntor', status: 'done' })
      expect((await db.tasks.get(id))?.changeSeq).toBe(server.tasks.get(id)!.changeSeq)
      expect((await readStatus(db))?.state).toBe('synced')
    })

    it('conflito entre dois aparelhos é resolvido por campo: os dois convergem', async () => {
      const celular = await open()
      const desktop = await open()
      const id = crypto.randomUUID()
      await saveLocalEdit(celular, id, { fields: { title: 'Revisar quadro' } }, T1)
      await runCycle(userId, celular, never, server)
      await runCycle(userId, desktop, never, server)

      // Os dois offline: o celular conclui, o desktop muda o título.
      await saveLocalEdit(celular, id, { fields: { status: 'done' } }, T2)
      await saveLocalEdit(desktop, id, { fields: { title: 'Revisar quadro do 2º andar' } }, T3)
      await runCycle(userId, celular, never, server)
      await runCycle(userId, desktop, never, server)
      await runCycle(userId, celular, never, server)

      for (const db of [celular, desktop]) {
        expect(await db.tasks.get(id)).toMatchObject({ title: 'Revisar quadro do 2º andar', status: 'done' })
      }
    })

    it('exclusão feita em um aparelho chega ao outro', async () => {
      const a = await open()
      const b = await open()
      const id = crypto.randomUUID()
      await saveLocalEdit(a, id, { fields: { title: 'Temporária' } }, T1)
      await runCycle(userId, a, never, server)
      await runCycle(userId, b, never, server)

      await saveLocalEdit(b, id, { delete: true }, T2)
      await runCycle(userId, b, never, server)
      await runCycle(userId, a, never, server)

      expect((await a.tasks.get(id))?.deletedAt).toBe(T2)
    })

    it('edição feita durante o ciclo não é sobrescrita pelo pull e continua na fila', async () => {
      const db = await open()
      const id = crypto.randomUUID()
      await saveLocalEdit(db, id, { fields: { title: 'Antes' } }, T1)
      // A página do pull já foi lida com o título antigo quando o usuário edita.
      server.beforePullReturns = async () => {
        server.beforePullReturns = undefined
        await saveLocalEdit(db, id, { fields: { title: 'Durante o ciclo' } }, T2)
      }

      await runCycle(userId, db, never, server)

      expect(server.tasks.get(id)?.title).toBe('Antes')
      expect((await db.tasks.get(id))?.title).toBe('Durante o ciclo')
      expect(await db.pendingOps.count()).toBe(1)
    })

    it('primeira sincronização traz tudo em páginas e grava o cursor', async () => {
      for (let i = 0; i < 1203; i++) {
        const id = crypto.randomUUID()
        server.tasks.set(id, serverTask(id, { title: `tarefa ${i}`, changeSeq: ++server.seq }))
      }
      const db = await open()
      expect(await readCursor(db)).toBeUndefined()

      expect(await runCycle(userId, db, never, server)).toBe('synced')

      expect(await db.tasks.count()).toBe(1203)
      expect(await readCursor(db)).toBe(1203)
      expect(server.pulls).toBe(3)
    })

    it.each([
      [401 as const, 'expired'],
      [409 as const, 'expired'],
      [500 as const, 'error'],
    ])('resposta %s: estado %s e a fila fica intacta', async (mode, expected) => {
      const db = await open()
      await saveLocalEdit(db, crypto.randomUUID(), { fields: { title: 'Guardada' } }, T1)
      server.mode = mode

      expect(await runCycle(userId, db, never, server)).toBe(expected)

      expect(await db.pendingOps.count()).toBe(1)
      expect((await readStatus(db))?.state).toBe(expected)
    })

    it('lote com operação aceita e recusada na mesma tarefa: fica o estado do servidor ao fim do lote', async () => {
      const db = await open()
      const id = crypto.randomUUID()
      await saveLocalEdit(db, id, { fields: { title: 'Original' } }, T1)
      await runCycle(userId, db, never, server)
      await saveLocalEdit(db, id, { fields: { priority: 'high' } }, T2)
      // Operação adulterada direto na fila (o formulário não deixaria passar).
      await db.pendingOps.add({ opId: crypto.randomUUID(), taskId: id, createdAt: T1, fields: { title: { value: 'x'.repeat(201), ts: T3 } }, tags: {}, deletedAt: null, queuedAt: T3 })

      expect(await runCycle(userId, db, never, server)).toBe('synced')

      expect(await db.tasks.get(id)).toMatchObject({ title: 'Original', priority: 'high' })
      expect(await db.pendingOps.count()).toBe(0)
      expect(await readRejections(db)).toMatchObject([{ title: 'Original', discarded: false }])
    })

    it('login novo apaga o "Sessão expirada" gravado no banco do usuário', async () => {
      const db = await open()
      server.mode = 401
      await runCycle(userId, db, never, server)
      expect((await readStatus(db))?.state).toBe('expired')

      await startSession({ id: db.name.replace('way2do-', ''), name: 'ana' })

      expect(await readStatus(db)).toBeUndefined()
    })

    it('não sincroniza o banco de um usuário que não é o da sessão', async () => {
      const db = await open()
      await saveLocalEdit(db, crypto.randomUUID(), { fields: { title: 'De outro' } }, T1)
      await startSession({ id: crypto.randomUUID(), name: 'bruno' })

      expect(await runCycle(userId, db, never, server)).toBe('aborted')

      expect(server.pushes + server.pulls).toBe(0)
    })

    it('resposta que não tira nada da fila é erro, não um laço infinito', async () => {
      const db = await open()
      await saveLocalEdit(db, crypto.randomUUID(), { fields: { title: 'X' } }, T1)
      const api: SyncApi = { push: async () => [], pull: server.pull.bind(server) }

      expect(await runCycle(userId, db, never, api)).toBe('error')
      expect(await db.pendingOps.count()).toBe(1)
    })
  })

  describe('agendamento', () => {
    afterEach(() => vi.useRealTimers())

    it('alteração local é enviada depois do debounce, sem esperar o ciclo periódico', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
      const db = await open()
      const stop = new AbortController()
      startSync(userId, db, stop.signal, { api: server })
      await vi.waitFor(() => expect(server.pulls).toBe(1)) // ciclo da abertura

      await saveLocalEdit(db, crypto.randomUUID(), { fields: { title: 'Nova' } }, T1)
      await vi.advanceTimersByTimeAsync(DEBOUNCE_MS - 500)
      expect(server.pushes).toBe(0)
      await vi.advanceTimersByTimeAsync(600)
      await vi.waitFor(() => expect(server.pushes).toBe(1), { interval: 1 })

      stop.abort()
    })
  })

  describe('fila e resposta do push', () => {
    it('divide a fila em lotes de até 500 operações e abaixo do limite de tamanho', async () => {
      const db = await open()
      const ops = Array.from({ length: 520 }, (_, i) => op(crypto.randomUUID(), { title: { value: `t${i}`, ts: T1 } }))
      await db.pendingOps.bulkAdd(ops)
      expect(await nextBatch(db)).toHaveLength(MAX_OPS_PER_PUSH)

      await db.pendingOps.clear()
      const big = 'x'.repeat(4000)
      await db.pendingOps.bulkAdd(Array.from({ length: 300 }, () => op(crypto.randomUUID(), { description: { value: big, ts: T1 } })))
      const batch = await nextBatch(db)
      expect(batch.length).toBeLessThan(300)
      expect(new TextEncoder().encode(JSON.stringify({ operations: batch })).length).toBeLessThan(1024 * 1024)
    })

    it('recusa: tira da fila, volta para o estado do servidor, reaplica a edição posterior e avisa', async () => {
      const db = await open()
      const id = crypto.randomUUID()
      await db.tasks.put(serverTask(id))
      await saveLocalEdit(db, id, { fields: { title: 'inválida (fingindo adulteração)' } }, T2)
      await saveLocalEdit(db, id, { fields: { priority: 'high' } }, T3)
      const [sent, later] = await db.pendingOps.orderBy('seq').toArray()

      const removed = await applyPushResults(db, [sent], [
        { opId: sent.opId, taskId: id, status: 'rejected', field: 'title', reason: 'no máximo 200 caracteres', task: serverTask(id) },
      ])

      expect(removed).toBe(1)
      expect((await db.pendingOps.toArray()).map((o) => o.opId)).toEqual([later.opId])
      expect(await db.tasks.get(id)).toMatchObject({ title: 'do servidor', priority: 'high' })
      expect(await readRejections(db)).toEqual([{ opId: sent.opId, title: 'do servidor', reason: 'no máximo 200 caracteres', discarded: false }])
    })

    it('recusa de tarefa que o servidor não tem: some do aparelho, com as edições dela na fila', async () => {
      const db = await open()
      const id = crypto.randomUUID()
      await saveLocalEdit(db, id, { fields: { title: 'Nova' } }, T1)
      const [sent] = await db.pendingOps.toArray()
      await saveLocalEdit(db, id, { fields: { status: 'done' } }, T2)

      await applyPushResults(db, [sent], [{ opId: sent.opId, taskId: id, status: 'rejected', field: 'title', reason: 'obrigatório', task: null }])

      expect(await db.tasks.get(id)).toBeUndefined()
      expect(await db.pendingOps.count()).toBe(0)
      expect(await readRejections(db)).toMatchObject([{ title: 'Nova', discarded: true }])
    })

    it('resultado de operação que não foi enviada é ignorado', async () => {
      const db = await open()
      const id = crypto.randomUUID()
      await saveLocalEdit(db, id, { fields: { title: 'A' } }, T1)
      const [sent] = await db.pendingOps.toArray()

      const removed = await applyPushResults(db, [sent], [{ opId: crypto.randomUUID(), taskId: id, status: 'accepted', field: null, reason: null, task: null }])

      expect(removed).toBe(0)
      expect(await db.pendingOps.count()).toBe(1)
    })
  })

  describe('aplicação do pull', () => {
    it('mescla por campo: a edição local mais nova fica, o resto vem do servidor', async () => {
      const db = await open()
      const id = crypto.randomUUID()
      await db.tasks.put(serverTask(id))
      await saveLocalEdit(db, id, { fields: { title: 'local, mais nova' } }, T3)

      await applyPulledPage(db, { tasks: [serverTask(id, { title: 'servidor', status: 'done', fieldTimestamps: { title: T2, status: T2 }, changeSeq: 7, version: 2 })], cursor: 7, hasMore: false })

      expect(await db.tasks.get(id)).toMatchObject({ title: 'local, mais nova', status: 'done', changeSeq: 7, version: 2 })
    })

    it('tarefa excluída que o aparelho nunca viu não é criada', async () => {
      const db = await open()
      const id = crypto.randomUUID()

      await applyPulledPage(db, { tasks: [serverTask(id, { deletedAt: T2 })], cursor: 3, hasMore: false })

      expect(await db.tasks.get(id)).toBeUndefined()
      expect(await readCursor(db)).toBe(3)
    })

    it('o cursor só avança', async () => {
      const db = await open()
      await applyPulledPage(db, { tasks: [], cursor: 10, hasMore: false })
      await applyPulledPage(db, { tasks: [], cursor: 4, hasMore: false })
      expect(await readCursor(db)).toBe(10)
    })

    it('horário do servidor à frente do relógio do aparelho não é cortado (as cópias não divergem)', async () => {
      const db = await open()
      const id = crypto.randomUUID()
      await db.tasks.put(serverTask(id))
      const future = new Date(Date.now() + 3_600_000).toISOString()

      await applyPulledPage(db, { tasks: [serverTask(id, { title: 'adiantado', fieldTimestamps: { title: future }, changeSeq: 2 })], cursor: 2, hasMore: false })

      expect((await db.tasks.get(id))?.fieldTimestamps.title).toBe(future)
    })

    it('tags somadas de dois aparelhos passando de 20: vale o servidor e o pull não trava', async () => {
      const db = await open()
      const id = crypto.randomUUID()
      const tags = (prefix: string, n: number) =>
        Object.fromEntries(Array.from({ length: n }, (_, i) => [`${prefix}${i}`, { present: true, ts: T1 }]))
      await db.tasks.put(serverTask(id, { tags: tags('a', 10) }))
      await saveLocalEdit(db, id, { tags: Object.fromEntries(Array.from({ length: 10 }, (_, i) => [`local${i}`, true])) }, T2)

      await applyPulledPage(db, { tasks: [serverTask(id, { tags: { ...tags('a', 10), ...tags('b', 10) }, changeSeq: 5 })], cursor: 5, hasMore: false })

      const task = await db.tasks.get(id)
      expect(Object.values(task!.tags).filter((t) => t.present)).toHaveLength(20)
      expect(Object.keys(task!.tags).some((t) => t.startsWith('local'))).toBe(false)
      expect(await db.pendingOps.count()).toBe(1) // continua na fila: o servidor recusa e avisa
      expect(await readCursor(db)).toBe(5)
    })
  })
})

describe('indicador', () => {
  const at = '2026-10-28T10:00:00.000Z'
  it.each([
    ['sessão expirada vence tudo', { state: 'expired' as const, at }, 3, false, 'expired'],
    ['navegador sem rede', { state: 'synced' as const, at }, 0, false, 'offline'],
    ['última requisição falhou por rede', { state: 'offline' as const, at }, 2, true, 'offline'],
    ['ciclo em andamento', { state: 'syncing' as const, at }, 2, true, 'syncing'],
    ['ainda sem nenhum ciclo', undefined, 0, true, 'syncing'],
    ['erro do servidor', { state: 'error' as const, at }, 1, true, 'error'],
    ['fila esperando o próximo ciclo', { state: 'synced' as const, at }, 2, true, 'pending'],
    ['tudo enviado', { state: 'synced' as const, at }, 0, true, 'ok'],
  ])('%s', (_caso, status, pending, online, expected) => {
    expect(indicatorKind(status, pending, online)).toBe(expected)
  })
})

describe('backoff', () => {
  it('dobra a cada falha, com jitter entre metade e o total, até o teto', () => {
    expect(backoffDelay(1, () => 0)).toBe(BACKOFF_MIN_MS / 2)
    expect(backoffDelay(1, () => 1)).toBe(BACKOFF_MIN_MS)
    expect(backoffDelay(3, () => 1)).toBe(BACKOFF_MIN_MS * 4)
    expect(backoffDelay(50, () => 1)).toBe(BACKOFF_MAX_MS)
  })
})

describe('tarefa vinda do servidor', () => {
  const raw = {
    id: '6F2F24A6-21F4-469F-B156-7AFC6EFA3280',
    title: 'X',
    description: null,
    dueDate: '2026-11-01',
    priority: 'high',
    status: 'pending',
    tags: { obra: { present: true, ts: '2026-10-28T10:00:00+00:00' } },
    fieldTimestamps: { title: '2026-10-28T10:00:00.1234567+00:00' },
    createdAt: '2026-10-28T10:00:00+00:00',
    deletedAt: null,
    version: 1,
    changeSeq: 9,
  }

  it('normaliza id e horários para o formato do banco local', () => {
    const task = toLocalTask(raw)
    expect(task.id).toBe('6f2f24a6-21f4-469f-b156-7afc6efa3280')
    expect(task.fieldTimestamps.title).toBe('2026-10-28T10:00:00.123Z')
    expect(task.tags.obra.ts).toBe('2026-10-28T10:00:00.000Z')
  })

  it.each([
    ['prioridade fora da lista', { priority: 'urgent' }],
    ['campo desconhecido', { fieldTimestamps: { isAdmin: '2026-10-28T10:00:00Z' } }],
    ['horário inválido', { createdAt: 'ontem' }],
    ['id que não é UUID', { id: '../outro-banco' }],
  ])('recusa %s', (_caso, patch) => {
    expect(() => toLocalTask({ ...raw, ...patch })).toThrow()
  })

  it('tag "__proto__" vira tag comum, sem trocar o protótipo', () => {
    const task = toLocalTask(JSON.parse(JSON.stringify(raw).replace('"obra"', '"__proto__"')))
    expect(Object.hasOwn(task.tags, '__proto__')).toBe(true)
    expect(Object.getPrototypeOf(task.tags)).toBe(Object.prototype)
  })
})

describe('requisições de sincronização', () => {
  afterEach(() => vi.unstubAllGlobals())

  const respond = (response: Response | Error) => {
    const fetchMock = vi.fn(async () => {
      if (response instanceof Error) throw response
      return response
    })
    vi.stubGlobal('fetch', fetchMock)
    return fetchMock
  }

  it('envia o usuário dono do banco no cabeçalho', async () => {
    const fetchMock = respond(Response.json({ tasks: [], cursor: 0, hasMore: false }))
    await pull('user-1', 0, 500)
    const init = (fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1]
    expect((init.headers as Record<string, string>)['X-Way2Do-User']).toBe('user-1')
  })

  it('tela fechada no meio da requisição não é tratada como falta de rede', async () => {
    vi.stubGlobal('fetch', vi.fn((_url: string, init: RequestInit) => new Promise((_, reject) => {
      init.signal!.addEventListener('abort', () => reject(new DOMException('abortado', 'AbortError')))
    })))
    const screen = new AbortController()
    const request = pull('user-1', 0, 500, screen.signal)
    screen.abort()
    await expect(request).rejects.not.toBeInstanceOf(OfflineError)
  })

  it.each([
    ['sem rede', new TypeError('Failed to fetch'), OfflineError],
    ['401', new Response(null, { status: 401 }), SessionExpiredError],
    ['409', new Response(null, { status: 409 }), OtherUserError],
    ['502 do proxy', new Response(null, { status: 502 }), ServerError],
    ['corpo fora do contrato', Response.json({ results: 'x' }), ServerError],
  ])('%s', async (_caso, response, error) => {
    respond(response)
    await expect(push('user-1', [])).rejects.toBeInstanceOf(error)
  })
})

function op(taskId: string, fields: PendingOp['fields']): PendingOp {
  return { opId: crypto.randomUUID(), taskId, createdAt: T1, fields, tags: {}, deletedAt: null, queuedAt: T1 }
}
