import { applyChange, normalizeTag, setTag, visibleTags, type FieldName, type MergeResult } from '../domain/merge'
import { emptyTask, type FieldChange, type Priority, type TagState, type Task, type TodoChange } from '../domain/types'
import type { UserDb } from './db'
import { uuid } from './ids'

/** Edição feita na interface: campos com o novo valor, tags adicionadas (true) ou removidas (false), exclusão. */
export interface LocalEdit {
  fields?: Partial<Record<FieldName, string | null>>
  tags?: Record<string, boolean>
  delete?: boolean
}

/** O que o formulário tem ao salvar. */
export interface FormValues {
  title: string
  description: string
  dueDate: string
  priority: Priority
  tags: string[]
}

/**
 * Só o que mudou em relação à tarefa como estava quando o formulário abriu (`snapshot`; `null` para tarefa nova),
 * campo a campo e tag a tag. É disso que o merge por campo depende: um campo não tocado não ganha horário novo e não
 * sobrescreve uma edição feita em outro aparelho. Descrição só com espaços e data vazia valem como ausentes.
 */
export function diffEdit(snapshot: Task | null, values: FormValues): LocalEdit {
  const fields: Partial<Record<FieldName, string | null>> = {}
  const desc = values.description.trim() ? values.description : null
  const due = values.dueDate || null
  if (snapshot === null || values.title !== snapshot.title) fields.title = values.title
  if (snapshot === null ? desc !== null : desc !== snapshot.description) fields.description = desc
  if (snapshot === null ? due !== null : due !== snapshot.dueDate) fields.dueDate = due
  if (snapshot === null || values.priority !== snapshot.priority) fields.priority = values.priority

  const before = new Set(snapshot ? visibleTags(snapshot) : [])
  const after = new Set(values.tags)
  const tags: Record<string, boolean> = {}
  for (const t of after) if (!before.has(t)) tags[t] = true
  for (const t of before) if (!after.has(t)) tags[t] = false
  return { fields, tags }
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
