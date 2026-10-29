import { applyChange, normalizeTag, setTag, type FieldName, type MergeResult } from '../domain/merge'
import { emptyTask, type FieldChange, type TagState, type TodoChange } from '../domain/types'
import type { UserDb } from './db'
import { uuid } from './ids'

/** Edição feita na interface: campos com o novo valor, tags adicionadas (true) ou removidas (false), exclusão. */
export interface LocalEdit {
  fields?: Partial<Record<FieldName, string | null>>
  tags?: Record<string, boolean>
  delete?: boolean
}

/**
 * Grava uma edição local: aplica o mesmo merge do servidor sobre a cópia local e, na mesma transação do IndexedDB,
 * enfileira a operação para o push. Edição inválida lança InvalidChangeError e nada é gravado.
 */
export async function saveLocalEdit(db: UserDb, taskId: string, edit: LocalEdit, now = new Date().toISOString()): Promise<MergeResult> {
  const fields: Record<string, FieldChange> = {}
  for (const [name, value] of Object.entries(edit.fields ?? {})) fields[name] = { value, ts: now }
  const tags: Record<string, TagState> = {}
  for (const [name, present] of Object.entries(edit.tags ?? {})) setTag(tags, normalizeTag(name), { present, ts: now })
  const change: TodoChange = { fields, tags, deletedAt: edit.delete ? now : null }

  return db.transaction('rw', db.tasks, db.pendingOps, async () => {
    const task = structuredClone((await db.tasks.get(taskId)) ?? emptyTask(taskId))
    const result = applyChange(task, { ...change, createdAt: task.createdAt ?? now }, now)
    if (!result.changed) return result

    await db.tasks.put(task)
    await db.pendingOps.add({
      opId: uuid(),
      taskId,
      createdAt: task.createdAt,
      fields,
      tags,
      deletedAt: change.deletedAt ?? null,
      queuedAt: now,
    })
    return result
  })
}

/** Gera o id da tarefa no aparelho: permite criar offline sem colisão e torna o push idempotente. */
export function newTaskId(): string {
  return uuid()
}
