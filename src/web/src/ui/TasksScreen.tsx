import { useLiveQuery } from 'dexie-react-hooks'
import { useCallback, useEffect, useMemo, useState } from 'react'
import type { User } from '../api/auth'
import { sortWithUrgency } from '../domain/ordering'
import type { Task } from '../domain/types'
import { userDb } from '../data/db'
import { newTaskId, saveLocalEdit, type LocalEdit } from '../data/localTasks'
import { startSync } from '../sync/engine'
import { indicatorKind, useBrowserOnline } from '../sync/indicator'
import { clearRejections, readCursor, readRejections, readStatus, type Rejection } from '../sync/store'
import { activeFilterCount, filterTasks, NO_FILTERS, tagCounts, type Filters } from './filters'
import { PriorityField, SearchField, StatusField, TagField } from './FiltersControls'
import { AlertIcon, CheckIcon, ClipboardIcon, CloseIcon, FilterIcon, InfoIcon, LogOutIcon, MenuIcon, PlusIcon, TagIcon, TrashIcon } from './icons'
import { Overlay } from './Overlay'
import { TaskForm } from './TaskForm'
import { SyncBar } from './SyncBar'
import { TaskItem } from './TaskItem'
import { useIsDesktop } from './useMediaQuery'

type Panel =
  | { kind: 'none' }
  | { kind: 'form'; taskId: string | null }
  | { kind: 'delete'; taskId: string }
  | { kind: 'filters' }
  | { kind: 'tags' }
  | { kind: 'menu' }
  | { kind: 'logout'; pending: number }

export function TasksScreen({ user, onLogout, onSessionExpired }: { user: User; onLogout: () => Promise<void>; onSessionExpired: () => Promise<void> }) {
  const desktop = useIsDesktop()
  const db = userDb(user.id)
  const stored = useLiveQuery(() => db.tasks.toArray(), [db])
  const pendingOps = useLiveQuery(() => db.pendingOps.count(), [db]) ?? 0
  const sync = useLiveQuery(async () => ({ status: await readStatus(db), cursor: await readCursor(db), rejections: await readRejections(db) }), [db])
  const browserOnline = useBrowserOnline()

  // Motor de sincronização deste usuário: para quando a tela fecha (logout, troca de usuário em outra aba).
  useEffect(() => {
    const stop = new AbortController()
    startSync(user.id, db, stop.signal)
    return () => stop.abort()
  }, [user.id, db])
  const [filters, setFilters] = useState<Filters>(NO_FILTERS)
  const [panel, setPanel] = useState<Panel>({ kind: 'none' })
  const [failure, setFailure] = useState<string | null>(null)
  const close = useCallback(() => setPanel({ kind: 'none' }), [])

  const alive = useMemo(() => (stored ?? []).filter((t) => t.deletedAt === null), [stored])
  const visible = useMemo(() => sortWithUrgency(filterTasks(alive, filters)), [alive, filters])
  const tags = useMemo(() => tagCounts(alive), [alive])
  const pending = visible.filter((e) => e.task.status === 'pending')
  const done = visible.filter((e) => e.task.status === 'done')
  const totals = { pending: alive.filter((t) => t.status === 'pending').length, done: alive.filter((t) => t.status === 'done').length }
  const filterCount = activeFilterCount(filters)

  // Falha do banco local (cota cheia, transação abortada): aviso visível em vez de erro só no console.
  const guarded = async (action: () => Promise<unknown>) => {
    try {
      await action()
      setFailure(null)
    } catch {
      setFailure('Não foi possível salvar neste aparelho. Tente de novo.')
    }
  }
  const toggle = (t: Task) => guarded(() => saveLocalEdit(db, t.id, { fields: { status: t.status === 'done' ? 'pending' : 'done' } }))
  const save = async (taskId: string | null, edit: LocalEdit) => {
    await saveLocalEdit(db, taskId ?? newTaskId(), edit)
    close()
  }
  const remove = async (taskId: string) => {
    await guarded(() => saveLocalEdit(db, taskId, { delete: true }))
    close()
  }
  const askLogout = () => (pendingOps > 0 ? setPanel({ kind: 'logout', pending: pendingOps }) : onLogout())

  if (stored === undefined || sync === undefined) return null // primeira leitura do IndexedDB (instantânea na prática)

  const indicator = indicatorKind(sync.status, pendingOps, browserOnline)
  // Primeira sincronização neste aparelho: a lista espera o pull completo, a menos que ele já tenha falhado.
  const firstSync = sync.cursor === undefined && (indicator === 'syncing' || indicator === 'ok' || indicator === 'pending')
  const syncBar = (desk: boolean) => <SyncBar kind={indicator} pending={pendingOps} desktop={desk} onReLogin={() => void onSessionExpired()} />

  const list = (
    <>
      {failure && (
        <div className="notice notice--danger" role="alert">
          <InfoIcon />
          <div className="notice__text">{failure}</div>
          <button className="iconbtn" type="button" aria-label="Fechar aviso" onClick={() => setFailure(null)}><CloseIcon /></button>
        </div>
      )}
      {sync.rejections.length > 0 && <RejectedNotice rejections={sync.rejections} onClose={() => void clearRejections(db)} />}
      {firstSync && alive.length === 0 ? (
        <div className="empty" role="status">
          <span className="empty__icon"><ClipboardIcon /></span>
          <p className="empty__title">Carregando suas tarefas…</p>
          <p>Na primeira vez neste aparelho, as tarefas vêm do servidor. Depois funcionam mesmo sem internet.</p>
        </div>
      ) : alive.length === 0 ? (
        <EmptyState onCreate={() => setPanel({ kind: 'form', taskId: null })} />
      ) : visible.length === 0 ? (
        <div className="empty">
          <p className="empty__title">Nenhuma tarefa com esses filtros</p>
          <button className="btn btn--secondary" type="button" onClick={() => setFilters(NO_FILTERS)}>Limpar filtros</button>
        </div>
      ) : (
        <>
          {pending.length > 0 && <TaskGroup label={`Pendentes (${pending.length})`} tasks={pending} desktop={desktop} onToggle={toggle} onOpen={(id) => setPanel({ kind: 'form', taskId: id })} />}
          {done.length > 0 && <TaskGroup label={`Concluídas (${done.length})`} tasks={done} desktop={desktop} onToggle={toggle} onOpen={(id) => setPanel({ kind: 'form', taskId: id })} />}
        </>
      )}
    </>
  )

  const summary = `${totals.pending} ${totals.pending === 1 ? 'pendente' : 'pendentes'} · ${totals.done} ${totals.done === 1 ? 'concluída' : 'concluídas'}`

  return (
    <div className="w2">
      {desktop ? (
        <div className="desk">
          {syncBar(true)}
          <header className="d-header">
            <div className="brand">
              <span className="brand__mark"><CheckIcon /></span>
              Way2Do
            </div>
            <nav className="d-nav" aria-label="Principal">
              <span className="d-navlink d-navlink--on" aria-current="page">Tarefas</span>
              <span className="d-user"><span className="avatar">{user.name.slice(0, 1).toUpperCase()}</span>{user.name}</span>
              <button className="d-navlink" type="button" onClick={askLogout}><LogOutIcon />Sair</button>
            </nav>
          </header>
          <div className="d-wrap">
            <aside className="d-side">
              <nav className="panel panel--tags" aria-label="Tags">
                <h2 className="section-label">Tags</h2>
                <TagList tags={tags} total={alive.length} selected={filters.tag} onSelect={(tag) => setFilters({ ...filters, tag })} />
              </nav>
            </aside>
            <main className="d-main">
              <div className="page-head">
                <div>
                  <h1 className="page-title">Minhas tarefas</h1>
                  <p className="hint">{summary}</p>
                </div>
                <button className="btn btn--primary" type="button" onClick={() => setPanel({ kind: 'form', taskId: null })}><PlusIcon />Nova tarefa</button>
              </div>
              <div className="panel toolbar" role="search">
                <SearchField value={filters.query} onChange={(query) => setFilters({ ...filters, query })} />
                <StatusField value={filters.status} onChange={(status) => setFilters({ ...filters, status })} />
                <PriorityField compact value={filters.priority} onChange={(priority) => setFilters({ ...filters, priority })} />
              </div>
              {list}
            </main>
          </div>
        </div>
      ) : (
        <div className="m-app">
          <div className="m-top">
            {syncBar(false)}
            <header className="appbar">
              <div className="appbar__title">
                <h1 className="brand brand--title">Minhas tarefas</h1>
                <span className="appbar__sub">{summary}</span>
              </div>
              <button className="iconbtn" type="button" aria-label="Menu da conta" onClick={() => setPanel({ kind: 'menu' })}><MenuIcon /></button>
            </header>
          </div>
          <main className="scroll">{list}</main>
          <nav className="bottombar" aria-label="Ações">
            <button className="btn btn--secondary" type="button" onClick={() => setPanel({ kind: 'filters' })}>
              <FilterIcon />Filtros{filterCount > 0 && <span className="count">{filterCount}</span>}
            </button>
            <button className="btn btn--secondary" type="button" onClick={() => setPanel({ kind: 'tags' })}><TagIcon />Tags</button>
            <button className="btn btn--primary" type="button" onClick={() => setPanel({ kind: 'form', taskId: null })}><PlusIcon />Nova tarefa</button>
          </nav>
        </div>
      )}

      {panel.kind === 'form' && (
        <TaskForm
          key={panel.taskId ?? 'new'}
          task={panel.taskId ? (alive.find((t) => t.id === panel.taskId) ?? null) : null}
          removed={panel.taskId !== null && !alive.some((t) => t.id === panel.taskId)}
          knownTags={tags.map((t) => t.name)}
          desktop={desktop}
          onSave={(edit) => save(panel.taskId, edit)}
          onCancel={close}
          onDelete={panel.taskId ? () => setPanel({ kind: 'delete', taskId: panel.taskId! }) : undefined}
        />
      )}

      {panel.kind === 'delete' && (
        <Overlay
          title="Excluir esta tarefa?"
          desktop={desktop}
          small
          onClose={close}
          footer={
            <>
              <button className="btn btn--secondary" type="button" onClick={close}>Cancelar</button>
              <button className="btn btn--danger" type="button" onClick={() => remove(panel.taskId)}><TrashIcon />Excluir tarefa</button>
            </>
          }
        >
          <p>
            <strong>{alive.find((t) => t.id === panel.taskId)?.title}</strong> será apagada deste aparelho e dos outros onde você usa o
            Way2Do. Não dá para desfazer.
          </p>
        </Overlay>
      )}

      {panel.kind === 'filters' && (
        <Overlay
          title="Filtros"
          desktop={false}
          onClose={close}
          footer={
            <>
              <button className="btn btn--secondary" type="button" onClick={() => setFilters(NO_FILTERS)}>Limpar</button>
              <button className="btn btn--primary" type="button" onClick={close}>Ver {visible.length} {visible.length === 1 ? 'tarefa' : 'tarefas'}</button>
            </>
          }
        >
          <SearchField hint value={filters.query} onChange={(query) => setFilters({ ...filters, query })} />
          <StatusField value={filters.status} onChange={(status) => setFilters({ ...filters, status })} />
          <PriorityField value={filters.priority} onChange={(priority) => setFilters({ ...filters, priority })} />
          <TagField value={filters.tag} tags={tags} onChange={(tag) => setFilters({ ...filters, tag })} />
        </Overlay>
      )}

      {panel.kind === 'tags' && (
        <Overlay title="Tags" desktop={false} onClose={close}>
          <TagList tags={tags} total={alive.length} selected={filters.tag} onSelect={(tag) => { setFilters({ ...filters, tag }); close() }} />
        </Overlay>
      )}

      {panel.kind === 'menu' && (
        <Overlay title={user.name} desktop={false} onClose={close}>
          <ul className="menu menu--flat">
            <li>
              <button className="menu__item menu__item--danger" type="button" onClick={() => { close(); void askLogout() }}><LogOutIcon />Sair</button>
            </li>
          </ul>
        </Overlay>
      )}

      {panel.kind === 'logout' && (
        <Overlay
          title="Sair com alterações pendentes?"
          desktop={desktop}
          small
          onClose={close}
          footer={
            <>
              <button className="btn btn--secondary" type="button" onClick={close}>Cancelar</button>
              <button className="btn btn--danger" type="button" onClick={() => void onLogout()}>Sair mesmo assim</button>
            </>
          }
        >
          <p>
            Há <strong>{panel.pending} {panel.pending === 1 ? 'alteração não sincronizada' : 'alterações não sincronizadas'}</strong> neste aparelho.
            Elas ficam guardadas e serão enviadas no seu próximo login.
          </p>
        </Overlay>
      )}
    </div>
  )
}

function TaskGroup({ label, tasks, desktop, onToggle, onOpen }: { label: string; tasks: { task: Task; urgent: boolean }[]; desktop: boolean; onToggle: (t: Task) => void; onOpen: (id: string) => void }) {
  return (
    <section className="group">
      <h2 className="group-label">{label}</h2>
      <ul className="tasklist">
        {tasks.map(({ task, urgent }) => (
          <TaskItem key={task.id} task={task} urgent={urgent} desktop={desktop} onToggle={() => onToggle(task)} onOpen={() => onOpen(task.id)} />
        ))}
      </ul>
    </section>
  )
}

function TagList({ tags, total, selected, onSelect }: { tags: { name: string; count: number }[]; total: number; selected: string | null; onSelect: (tag: string | null) => void }) {
  return (
    <ul className="taglist">
      <li>
        <button className={selected === null ? 'taglink taglink--on' : 'taglink'} type="button" aria-pressed={selected === null} onClick={() => onSelect(null)}>
          Todas<span className="taglink__n">{total}</span>
        </button>
      </li>
      {tags.map((t) => (
        <li key={t.name}>
          <button className={selected === t.name ? 'taglink taglink--on' : 'taglink'} type="button" aria-pressed={selected === t.name} onClick={() => onSelect(t.name)}>
            <TagIcon size="sm" />
            {t.name}
            <span className="taglink__n">{t.count}</span>
          </button>
        </li>
      ))}
    </ul>
  )
}

function EmptyState({ onCreate }: { onCreate: () => void }) {
  return (
    <div className="empty">
      <span className="empty__icon"><ClipboardIcon /></span>
      <p className="empty__title">Nenhuma tarefa ainda</p>
      <p>Crie a primeira. Ela fica salva neste aparelho e funciona mesmo sem internet.</p>
      <button className="btn btn--primary" type="button" onClick={onCreate}><PlusIcon />Criar primeira tarefa</button>
    </div>
  )
}

/** Alterações recusadas pelo servidor: a tarefa voltou para a versão dele (escopo, seção 5). */
function RejectedNotice({ rejections, onClose }: { rejections: Rejection[]; onClose: () => void }) {
  const shown = rejections.slice(-3)
  const restored = rejections.filter((r) => !r.discarded).length
  const footer = restored === 0 ? null
    : restored < rejections.length ? 'As outras voltaram para a versão salva no servidor.'
    : restored === 1 ? 'A tarefa voltou para a versão salva no servidor.'
    : 'As tarefas voltaram para a versão salva no servidor.'
  return (
    <div className="notice notice--warn" role="alert">
      <AlertIcon />
      <div className="notice__text">
        <div className="notice__title">
          {rejections.length === 1 ? 'Uma alteração foi recusada pelo servidor' : `${rejections.length} alterações foram recusadas pelo servidor`}
        </div>
        <ul className="rejections">
          {shown.map((r) => (
            <li key={r.opId}><strong>{r.title}</strong>: {r.reason}{r.discarded && '. Descartada deste aparelho: o servidor não a aceitou.'}</li>
          ))}
        </ul>
        {footer}
      </div>
      <button className="iconbtn" type="button" aria-label="Fechar aviso" onClick={onClose}><CloseIcon /></button>
    </div>
  )
}
