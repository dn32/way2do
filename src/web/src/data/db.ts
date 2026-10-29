import Dexie, { type Table } from 'dexie'
import type { FieldChange, TagState, Task } from '../domain/types'

/** Operação da fila: o que mudou numa tarefa, no formato do push (escopo, seção 8). */
export interface PendingOp {
  seq?: number
  opId: string
  taskId: string
  createdAt: string | null
  fields: Record<string, FieldChange>
  tags: Record<string, TagState>
  deletedAt: string | null
  queuedAt: string
}

export interface MetaEntry {
  key: string
  value: unknown
}

/**
 * Banco local de um usuário (`way2do-<userId>`): dois usuários no mesmo aparelho nunca veem os dados um do outro.
 * É a fonte da verdade da interface; a rede nunca bloqueia uma ação.
 */
export class UserDb extends Dexie {
  tasks!: Table<Task, string>
  pendingOps!: Table<PendingOp, number>
  meta!: Table<MetaEntry, string>

  constructor(userId: string) {
    super(`way2do-${userId}`)
    this.version(1).stores({
      tasks: 'id, changeSeq',
      pendingOps: '++seq, taskId',
      meta: 'key',
    })
  }
}

/** Usuário logado neste aparelho: só dados não secretos (o token fica no cookie HttpOnly). */
export interface Session {
  key: 'current'
  userId: string
  name: string
}

/** Marcas do aparelho, independentes de usuário (ex.: logout feito sem conexão, ainda não enviado ao servidor). */
export interface DeviceFlag {
  key: 'pendingLogout'
  value: true
}

class SessionDb extends Dexie {
  session!: Table<Session, string>
  flags!: Table<DeviceFlag, string>

  constructor() {
    super('way2do-session')
    this.version(1).stores({ session: 'key' })
    this.version(2).stores({ session: 'key', flags: 'key' })
  }
}

export const sessionDb = new SessionDb()

const userDbs = new Map<string, UserDb>()

export function userDb(userId: string): UserDb {
  let db = userDbs.get(userId)
  if (!db) {
    db = new UserDb(userId)
    userDbs.set(userId, db)
  }
  return db
}
