import type { Task } from '../domain/types'
import type { Rejection } from '../sync/store'
import type { TaskGroups } from './filters'
import { AlertIcon, ClipboardIcon, CloseIcon, InfoIcon, PlusIcon, TagIcon } from './icons'
import { TaskItem } from './TaskItem'

/**
 * Conteúdo da lista de tarefas: avisos (falha local, recusas do servidor) e um destes estados — primeira
 * sincronização, nenhuma tarefa, nenhuma tarefa com os filtros, ou os grupos Pendentes e Concluídas.
 */
export function TaskListContent({
  groups,
  firstSync,
  failure,
  rejections,
  desktop,
  onCloseFailure,
  onCloseRejections,
  onToggle,
  onOpen,
  onCreate,
  onClearFilters,
}: {
  groups: TaskGroups
  firstSync: boolean
  failure: string | null
  rejections: Rejection[]
  desktop: boolean
  onCloseFailure: () => void
  onCloseRejections: () => void
  onToggle: (t: Task) => void
  onOpen: (id: string) => void
  onCreate: () => void
  onClearFilters: () => void
}) {
  const { alive, visible, pending, done } = groups
  return (
    <>
      {failure && (
        <div className="notice notice--danger" role="alert">
          <InfoIcon />
          <div className="notice__text">{failure}</div>
          <button className="iconbtn" type="button" aria-label="Fechar aviso" onClick={onCloseFailure}><CloseIcon /></button>
        </div>
      )}
      {rejections.length > 0 && <RejectedNotice rejections={rejections} onClose={onCloseRejections} />}
      {firstSync && alive.length === 0 ? (
        <div className="empty" role="status">
          <span className="empty__icon"><ClipboardIcon /></span>
          <p className="empty__title">Carregando suas tarefas…</p>
          <p>Na primeira vez neste aparelho, as tarefas vêm do servidor. Depois funcionam mesmo sem internet.</p>
        </div>
      ) : alive.length === 0 ? (
        <EmptyState onCreate={onCreate} />
      ) : visible.length === 0 ? (
        <div className="empty">
          <p className="empty__title">Nenhuma tarefa com esses filtros</p>
          <button className="btn btn--secondary" type="button" onClick={onClearFilters}>Limpar filtros</button>
        </div>
      ) : (
        <>
          {pending.length > 0 && <TaskGroup label={`Pendentes (${pending.length})`} tasks={pending} desktop={desktop} onToggle={onToggle} onOpen={onOpen} />}
          {done.length > 0 && <TaskGroup label={`Concluídas (${done.length})`} tasks={done} desktop={desktop} onToggle={onToggle} onOpen={onOpen} />}
        </>
      )}
    </>
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

/** Tags existentes para filtrar (lateral no desktop, painel no celular). */
export function TagList({ tags, total, selected, onSelect }: { tags: { name: string; count: number }[]; total: number; selected: string | null; onSelect: (tag: string | null) => void }) {
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
