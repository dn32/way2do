import { visibleTags } from '../domain/merge'
import type { Task } from '../domain/types'
import { dueBadge, PRIORITY_BADGE, PRIORITY_LABEL, PRIORITY_LEVEL } from './format'
import { AlertIcon, CalendarIcon, CheckIcon, PriorityBars, TagIcon } from './icons'

export function TaskItem({
  task,
  urgent,
  desktop,
  onToggle,
  onOpen,
}: {
  task: Task
  urgent: boolean
  desktop: boolean
  onToggle: () => void
  onOpen: () => void
}) {
  const done = task.status === 'done'
  const urgentBadge = urgent && (
    <span className="badge badge--urgent">
      <AlertIcon size="sm" />
      Urgente
    </span>
  )
  const title = (
    <button className="task__title" type="button" onClick={onOpen}>
      {task.title}
    </button>
  )

  return (
    <li className={`task${urgent && !done ? ' task--urgent' : ''}${done ? ' task--done' : ''}`}>
      <button
        className={`toggle${done ? ' toggle--done' : ''}`}
        type="button"
        aria-label={`${done ? 'Reabrir' : 'Concluir'}: ${task.title}`}
        onClick={onToggle}
      >
        <span className="toggle__ring">{done && <CheckIcon size="sm" />}</span>
      </button>
      <div className="task__body">
        {desktop ? (
          <div className="task__head">
            {urgentBadge}
            {title}
          </div>
        ) : (
          <>
            {urgentBadge && <div className="task__meta">{urgentBadge}</div>}
            {title}
          </>
        )}
        {task.description && !done && <p className="task__desc">{task.description}</p>}
        <div className="task__meta">
          <span className={PRIORITY_BADGE[task.priority]}>
            <PriorityBars level={PRIORITY_LEVEL[task.priority]} />
            {PRIORITY_LABEL[task.priority]}
          </span>
          {task.dueDate && <DueBadge dueDate={task.dueDate} done={done} />}
          {visibleTags(task).map((tag) => (
            <span key={tag} className="tag">
              <TagIcon size="sm" />
              {tag}
            </span>
          ))}
        </div>
      </div>
    </li>
  )
}

function DueBadge({ dueDate, done }: { dueDate: string; done: boolean }) {
  const badge = dueBadge(dueDate, done ? 'done' : 'pending')
  return (
    <span className={badge.className}>
      <CalendarIcon />
      {badge.label}
    </span>
  )
}
