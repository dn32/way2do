import { useLiveQuery } from 'dexie-react-hooks'
import { useCallback, useEffect, useMemo, useState } from 'react'
import type { User } from '../api/auth'
import type { Task } from '../domain/types'
import { userDb } from '../data/db'
import { newTaskId, saveLocalEdit, type LocalEdit } from '../data/localTasks'
import { startSync } from '../sync/engine'
import { indicatorKind, isFirstSync } from '../sync/indicator'
import { clearRejections, readCursor, readRejections, readStatus } from '../sync/store'
import { activeFilterCount, arrangeTasks, NO_FILTERS, summarizeTasks, type Filters } from './filters'
import { PriorityField, SearchField, StatusField } from './FiltersControls'
import { CheckIcon, FilterIcon, LogOutIcon, MenuIcon, PlusIcon, TagIcon } from './icons'
import { IntegrationsPanel } from './IntegrationsPanel'
import { SyncBar } from './SyncBar'
import { TaskForm } from './TaskForm'
import { TagList, TaskListContent } from './TaskList'
import { AccountMenu, DeleteTaskDialog, FiltersSheet, LogoutDialog, TagsSheet, type View } from './TasksDialogs'
import { useBrowserOnline } from './useBrowserOnline'
import { useIsDesktop } from './useMediaQuery'

type Panel =
  | { kind: 'none' }
  | { kind: 'form'; taskId: string | null }
  | { kind: 'delete'; taskId: string }
  | { kind: 'filters' }
  | { kind: 'tags' }
  | { kind: 'menu' }
  | { kind: 'logout'; pending: number }

/**
 * Tela principal de um usuário logado: lê o banco local, mantém o motor de sincronização ligado, guarda o estado da
 * tela (filtros, painel aberto, aba) e monta o layout do desktop ou do celular. O conteúdo da lista e os diálogos
 * são componentes que só recebem props (TaskList.tsx e TasksDialogs.tsx).
 */
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
  const [view, setView] = useState<View>('tasks')
  const [failure, setFailure] = useState<string | null>(null)
  const close = useCallback(() => setPanel({ kind: 'none' }), [])
  const patchFilters = useCallback((patch: Partial<Filters>) => setFilters((f) => ({ ...f, ...patch })), [])

  // Tags e totais dependem só do banco; a lista, também dos filtros (uma tecla na busca não recalcula as tags).
  const summary = useMemo(() => summarizeTasks(stored ?? []), [stored])
  const arrangement = useMemo(() => arrangeTasks(summary.alive, filters), [summary, filters])
  const groups = useMemo(() => ({ ...summary, ...arrangement }), [summary, arrangement])
  const { alive, visible, totals, tags } = groups

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
  const openForm = (taskId: string | null) => setPanel({ kind: 'form', taskId })

  if (stored === undefined || sync === undefined) return null // primeira leitura do IndexedDB (instantânea na prática)

  const indicator = indicatorKind(sync.status, pendingOps, browserOnline)
  const syncBar = (desk: boolean) => <SyncBar kind={indicator} pending={pendingOps} desktop={desk} onReLogin={() => void onSessionExpired()} />
  const integrations = <IntegrationsPanel desktop={desktop} onSessionExpired={() => void onSessionExpired()} />
  const list = (
    <TaskListContent
      groups={groups}
      firstSync={isFirstSync(sync.cursor, indicator)}
      failure={failure}
      rejections={sync.rejections}
      desktop={desktop}
      onCloseFailure={() => setFailure(null)}
      onCloseRejections={() => void clearRejections(db)}
      onToggle={toggle}
      onOpen={openForm}
      onCreate={() => openForm(null)}
      onClearFilters={() => setFilters(NO_FILTERS)}
    />
  )
  const counts = `${totals.pending} ${totals.pending === 1 ? 'pendente' : 'pendentes'} · ${totals.done} ${totals.done === 1 ? 'concluída' : 'concluídas'}`
  const filterCount = activeFilterCount(filters)

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
              <NavLink label="Tarefas" on={view === 'tasks'} onClick={() => setView('tasks')} />
              <NavLink label="Integrações" on={view === 'integrations'} onClick={() => setView('integrations')} />
              <span className="d-user"><span className="avatar">{user.name.slice(0, 1).toUpperCase()}</span>{user.name}</span>
              <button className="d-navlink" type="button" onClick={askLogout}><LogOutIcon />Sair</button>
            </nav>
          </header>
          {view === 'integrations' ? (
            <div className="d-wrap">
              <main className="d-main">{integrations}</main>
            </div>
          ) : (
            <div className="d-wrap">
              <aside className="d-side">
                <nav className="panel panel--tags" aria-label="Tags">
                  <h2 className="section-label">Tags</h2>
                  <TagList tags={tags} total={alive.length} selected={filters.tag} onSelect={(tag) => patchFilters({ tag })} />
                </nav>
              </aside>
              <main className="d-main">
                <div className="page-head">
                  <div>
                    <h1 className="page-title">Minhas tarefas</h1>
                    <p className="hint">{counts}</p>
                  </div>
                  <button className="btn btn--primary" type="button" onClick={() => openForm(null)}><PlusIcon />Nova tarefa</button>
                </div>
                <div className="panel toolbar" role="search">
                  <SearchField value={filters.query} onChange={(query) => patchFilters({ query })} />
                  <StatusField value={filters.status} onChange={(status) => patchFilters({ status })} />
                  <PriorityField compact value={filters.priority} onChange={(priority) => patchFilters({ priority })} />
                </div>
                {list}
              </main>
            </div>
          )}
        </div>
      ) : (
        <div className="m-app">
          <div className="m-top">
            {syncBar(false)}
            <header className="appbar">
              <div className="appbar__title">
                <h1 className="brand brand--title">{view === 'tasks' ? 'Minhas tarefas' : 'Integrações'}</h1>
                <span className="appbar__sub">{view === 'tasks' ? counts : 'Sistemas que recebem as suas tarefas'}</span>
              </div>
              <button className="iconbtn" type="button" aria-label="Menu da conta" onClick={() => setPanel({ kind: 'menu' })}><MenuIcon /></button>
            </header>
          </div>
          <main className="scroll">{view === 'tasks' ? list : integrations}</main>
          {view === 'tasks' && (
            <nav className="bottombar" aria-label="Ações">
              <button className="btn btn--secondary" type="button" onClick={() => setPanel({ kind: 'filters' })}>
                <FilterIcon />Filtros{filterCount > 0 && <span className="count">{filterCount}</span>}
              </button>
              <button className="btn btn--secondary" type="button" onClick={() => setPanel({ kind: 'tags' })}><TagIcon />Tags</button>
              <button className="btn btn--primary" type="button" onClick={() => openForm(null)}><PlusIcon />Nova tarefa</button>
            </nav>
          )}
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
        <DeleteTaskDialog
          title={alive.find((t) => t.id === panel.taskId)?.title}
          desktop={desktop}
          onCancel={close}
          onConfirm={() => void remove(panel.taskId)}
        />
      )}
      {panel.kind === 'filters' && (
        <FiltersSheet filters={filters} tags={tags} visibleCount={visible.length} onChange={patchFilters} onClear={() => setFilters(NO_FILTERS)} onClose={close} />
      )}
      {panel.kind === 'tags' && (
        <TagsSheet tags={tags} total={alive.length} selected={filters.tag} onSelect={(tag) => { patchFilters({ tag }); close() }} onClose={close} />
      )}
      {panel.kind === 'menu' && (
        <AccountMenu
          userName={user.name}
          view={view}
          onNavigate={(v) => { setView(v); close() }}
          onLogout={() => { close(); void askLogout() }}
          onClose={close}
        />
      )}
      {panel.kind === 'logout' && <LogoutDialog pending={panel.pending} desktop={desktop} onCancel={close} onConfirm={() => void onLogout()} />}
    </div>
  )
}

function NavLink({ label, on, onClick }: { label: string; on: boolean; onClick: () => void }) {
  return (
    <button className={on ? 'd-navlink d-navlink--on' : 'd-navlink'} type="button" aria-current={on ? 'page' : undefined} onClick={onClick}>
      {label}
    </button>
  )
}
