import 'fake-indexeddb/auto'
import { afterEach, describe, expect, it } from 'vitest'
import { InvalidChangeError } from '../domain/merge'
import { UserDb } from './db'
import { newTaskId, saveLocalEdit } from './localTasks'

describe('escrita local', () => {
  let db: UserDb

  afterEach(async () => {
    await db.delete()
  })

  it('cria a tarefa e enfileira a operação na mesma transação', async () => {
    db = new UserDb(crypto.randomUUID())
    const id = newTaskId()

    const result = await saveLocalEdit(db, id, { fields: { title: 'Comprar EPI', priority: 'high' }, tags: { Obra: true } }, '2026-10-29T10:00:00.000Z')

    expect(result).toEqual({ changed: true, contentChanged: true })
    const task = await db.tasks.get(id)
    expect(task?.title).toBe('Comprar EPI')
    expect(task?.createdAt).toBe('2026-10-29T10:00:00.000Z')
    expect(task?.tags).toEqual({ obra: { present: true, ts: '2026-10-29T10:00:00.000Z' } })
    const ops = await db.pendingOps.toArray()
    expect(ops).toHaveLength(1)
    expect(ops[0]).toMatchObject({
      taskId: id,
      createdAt: '2026-10-29T10:00:00.000Z',
      fields: { title: { value: 'Comprar EPI', ts: '2026-10-29T10:00:00.000Z' } },
      tags: { obra: { present: true } },
      deletedAt: null,
    })
  })

  it('edição sem mudança não enfileira nada', async () => {
    db = new UserDb(crypto.randomUUID())
    const id = newTaskId()
    await saveLocalEdit(db, id, { fields: { title: 'A' } }, '2026-10-29T10:00:00.000Z')

    const result = await saveLocalEdit(db, id, { fields: { title: 'A' } }, '2026-10-29T09:00:00.000Z') // mais antiga

    expect(result.changed).toBe(false)
    expect(await db.pendingOps.count()).toBe(1)
  })

  it('edição inválida não grava a tarefa nem a operação', async () => {
    db = new UserDb(crypto.randomUUID())
    const id = newTaskId()
    await saveLocalEdit(db, id, { fields: { title: 'Original' } }, '2026-10-29T10:00:00.000Z')

    await expect(saveLocalEdit(db, id, { fields: { title: 'Novo', priority: 'xyz' } })).rejects.toBeInstanceOf(InvalidChangeError)

    expect((await db.tasks.get(id))?.title).toBe('Original')
    expect(await db.pendingOps.count()).toBe(1)
  })

  it('excluir marca a tarefa (tombstone) e enfileira a exclusão', async () => {
    db = new UserDb(crypto.randomUUID())
    const id = newTaskId()
    await saveLocalEdit(db, id, { fields: { title: 'Temporária' } }, '2026-10-29T10:00:00.000Z')

    await saveLocalEdit(db, id, { delete: true }, '2026-10-29T11:00:00.000Z')

    expect((await db.tasks.get(id))?.deletedAt).toBe('2026-10-29T11:00:00.000Z')
    const ops = await db.pendingOps.orderBy('seq').toArray()
    expect(ops.map((o) => o.deletedAt)).toEqual([null, '2026-10-29T11:00:00.000Z'])
  })
})
