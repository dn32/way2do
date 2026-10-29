export type Priority = 'low' | 'medium' | 'high'
export type Status = 'pending' | 'done'

/** Estado de uma tag no merge por item: presente ou removida, e quando (ISO 8601, UTC, em milissegundos). */
export interface TagState {
  present: boolean
  ts: string
}

/** Tarefa como fica no banco local e como chega no pull. Datas e horários em texto ISO 8601. */
export interface Task {
  id: string
  title: string
  description: string | null
  /** Data de calendário AAAA-MM-DD, sem fuso. */
  dueDate: string | null
  priority: Priority
  status: Status
  tags: Record<string, TagState>
  fieldTimestamps: Record<string, string>
  /** null enquanto a tarefa não foi criada (cópia vazia antes do primeiro merge). */
  createdAt: string | null
  deletedAt: string | null
  version: number
  changeSeq: number
}

/** Novo valor de um campo e o horário em que foi alterado. */
export interface FieldChange {
  value: unknown
  ts: string
}

/** Alteração de uma tarefa (operação da fila ou estado de outra cópia). Campos e tags ausentes não são tocados. */
export interface TodoChange {
  fields?: Record<string, FieldChange>
  tags?: Record<string, TagState>
  deletedAt?: string | null
  createdAt?: string | null
}

export function emptyTask(id: string): Task {
  return {
    id,
    title: '',
    description: null,
    dueDate: null,
    priority: 'medium',
    status: 'pending',
    tags: {},
    fieldTimestamps: {},
    createdAt: null,
    deletedAt: null,
    version: 0,
    changeSeq: 0,
  }
}
