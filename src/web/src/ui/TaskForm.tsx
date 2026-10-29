import { useState, type FormEvent } from 'react'
import { InvalidChangeError, LIMITS, normalizeTag, visibleTags, type FieldName } from '../domain/merge'
import type { Priority, Task } from '../domain/types'
import type { LocalEdit } from '../data/localTasks'
import { PRIORITY_LABEL } from './format'
import { CloseIcon, InfoIcon, TrashIcon } from './icons'
import { Overlay } from './Overlay'

const PRIORITIES: Priority[] = ['low', 'medium', 'high']

/**
 * Criar e editar tarefa. Ao salvar, monta só o que mudou (campo a campo e tag a tag): no merge por campo, um campo
 * não tocado não ganha timestamp novo e não sobrescreve edições feitas em outro aparelho.
 */
export function TaskForm({
  task: initialTask,
  removed,
  knownTags,
  desktop,
  onSave,
  onCancel,
  onDelete,
}: {
  task: Task | null
  /** A tarefa foi excluída (em outro aparelho ou aba) enquanto o formulário estava aberto. */
  removed: boolean
  knownTags: string[]
  desktop: boolean
  onSave: (edit: LocalEdit) => Promise<void>
  onCancel: () => void
  onDelete?: () => void
}) {
  // Retrato da tarefa quando o formulário abriu: é com ele que se compara o que o usuário mudou. Comparar com a versão
  // atual faria um campo não tocado "voltar" ao valor antigo e vencer uma edição feita em outro aparelho.
  const [task] = useState(initialTask)
  const [title, setTitle] = useState(task?.title ?? '')
  const [description, setDescription] = useState(task?.description ?? '')
  const [dueDate, setDueDate] = useState(task?.dueDate ?? '')
  const [priority, setPriority] = useState<Priority>(task?.priority ?? 'medium')
  const [tags, setTags] = useState<string[]>(task ? visibleTags(task) : [])
  const [tagInput, setTagInput] = useState('')
  const [errors, setErrors] = useState<Partial<Record<string, string>>>({})
  const [saving, setSaving] = useState(false)

  const isNew = task === null
  const heading = isNew ? 'Nova tarefa' : 'Editar tarefa'
  const formId = 'task-form'

  function addTag(raw: string) {
    try {
      const name = normalizeTag(raw)
      if (!tags.includes(name)) {
        if (tags.length >= LIMITS.maxTags) throw new InvalidChangeError('tags', `no máximo ${LIMITS.maxTags} tags`)
        setTags([...tags, name])
      }
      setTagInput('')
      setErrors((e) => ({ ...e, tags: undefined }))
    } catch (e) {
      if (e instanceof InvalidChangeError) setErrors((prev) => ({ ...prev, tags: e.reason }))
    }
  }

  function buildEdit(): LocalEdit {
    const fields: Partial<Record<FieldName, string | null>> = {}
    const desc = description.trim() ? description : null
    const due = dueDate || null
    if (task === null || title !== task.title) fields.title = title
    if (task === null ? desc !== null : desc !== task.description) fields.description = desc
    if (task === null ? due !== null : due !== task.dueDate) fields.dueDate = due
    if (task === null || priority !== task.priority) fields.priority = priority

    const before = new Set(task ? visibleTags(task) : [])
    const after = new Set(tags)
    const tagEdits: Record<string, boolean> = {}
    for (const t of after) if (!before.has(t)) tagEdits[t] = true
    for (const t of before) if (!after.has(t)) tagEdits[t] = false
    return { fields, tags: tagEdits }
  }

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (removed) return
    if (!title.trim()) {
      setErrors({ title: 'Informe um título para salvar.' })
      return
    }
    setSaving(true)
    try {
      await onSave(buildEdit())
    } catch (err) {
      if (err instanceof InvalidChangeError) {
        // Campos com lugar próprio mostram a mensagem ali; os demais, no aviso do topo.
        const shown = ['title', 'description', 'dueDate', 'tags'].includes(err.field)
        setErrors(shown ? { [err.field]: err.reason } : { form: `Não foi possível salvar: ${err.reason}.` })
      } else {
        setErrors({ form: 'Não foi possível salvar neste aparelho. Tente de novo.' })
      }
    } finally {
      setSaving(false)
    }
  }

  const suggestions = knownTags.filter((t) => !tags.includes(t)).slice(0, 8)

  const fieldsJsx = (
    <>
      {removed && (
        <div className="notice notice--warn" role="alert">
          <InfoIcon />
          <div>Esta tarefa foi excluída em outro aparelho. As alterações não podem mais ser salvas.</div>
        </div>
      )}
      {errors.form && (
        <div className="notice notice--danger" role="alert">
          <InfoIcon />
          <div>{errors.form}</div>
        </div>
      )}
      <div className="field">
        <label className="label" htmlFor="task-title">
          Título <span className="req">*</span>
        </label>
        <input
          className={errors.title ? 'input input--error' : 'input'}
          id="task-title"
          type="text"
          placeholder="O que precisa ser feito?"
          maxLength={LIMITS.maxTitle}
          value={title}
          onChange={(e) => setTitle(e.target.value)}
          aria-invalid={!!errors.title}
          aria-describedby={errors.title ? 'task-title-error' : undefined}
          autoFocus
        />
        {errors.title && (
          <span className="error-msg" id="task-title-error">
            <InfoIcon size="sm" />
            {errors.title}
          </span>
        )}
      </div>

      <div className="field">
        <label className="label" htmlFor="task-desc">Descrição</label>
        <textarea
          className={errors.description ? 'input input--error' : 'input'}
          id="task-desc"
          placeholder="Detalhes, endereço, contato…"
          maxLength={LIMITS.maxDescription}
          value={description}
          onChange={(e) => setDescription(e.target.value)}
        />
        <span className="hint">Escreva “urgente” no título ou na descrição para a tarefa subir ao topo.</span>
        {errors.description && <span className="error-msg">{errors.description}</span>}
      </div>

      <div className="field">
        <label className="label" htmlFor="task-due">Vencimento</label>
        <input
          className={errors.dueDate ? 'input input--error' : 'input'}
          id="task-due"
          type="date"
          value={dueDate}
          onChange={(e) => setDueDate(e.target.value)}
          aria-invalid={!!errors.dueDate}
        />
        {errors.dueDate && (
          <span className="error-msg">
            <InfoIcon size="sm" />
            {errors.dueDate}
          </span>
        )}
      </div>

      <div className="field">
        <span className="label" id="task-priority">Prioridade</span>
        <div className="seg" role="group" aria-labelledby="task-priority">
          {PRIORITIES.map((p) => (
            <button
              key={p}
              type="button"
              className={p === priority ? 'seg__opt seg__opt--on' : 'seg__opt'}
              aria-pressed={p === priority}
              onClick={() => setPriority(p)}
            >
              {PRIORITY_LABEL[p]}
            </button>
          ))}
        </div>
      </div>

      <div className="field">
        <label className="label" htmlFor="task-tag">Tags</label>
        {tags.length > 0 && (
          <div className="chips">
            {tags.map((t) => (
              <span key={t} className="chip">
                {t}
                <button className="chip__x" type="button" aria-label={`Remover a tag ${t}`} onClick={() => setTags(tags.filter((x) => x !== t))}>
                  <CloseIcon />
                </button>
              </span>
            ))}
          </div>
        )}
        <div className="row">
          <input
            className="input"
            id="task-tag"
            type="text"
            placeholder="Digite uma tag"
            maxLength={LIMITS.maxTag}
            value={tagInput}
            onChange={(e) => setTagInput(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault()
                if (tagInput.trim()) addTag(tagInput)
              }
            }}
          />
          <button className="btn btn--secondary" type="button" onClick={() => tagInput.trim() && addTag(tagInput)}>
            Adicionar
          </button>
        </div>
        {errors.tags && <span className="error-msg">{errors.tags}</span>}
        {suggestions.length > 0 && (
          <>
            <span className="hint">Tags que você já usa:</span>
            <div className="chips">
              {suggestions.map((t) => (
                <button key={t} className="btn btn--secondary btn--suggestion" type="button" onClick={() => addTag(t)}>
                  + {t}
                </button>
              ))}
            </div>
          </>
        )}
      </div>

      {!isNew && onDelete && !desktop && (
        <button className="btn btn--danger-outline btn--block" type="button" onClick={onDelete}>
          <TrashIcon />
          Excluir tarefa
        </button>
      )}
    </>
  )

  if (desktop) {
    return (
      <Overlay
        title={heading}
        desktop
        onClose={onCancel}
        footer={
          <>
            {!isNew && onDelete && (
              <button className="btn btn--danger-outline" type="button" onClick={onDelete}>
                <TrashIcon />
                Excluir
              </button>
            )}
            <span className="spacer" />
            <button className="btn btn--secondary" type="button" onClick={onCancel}>Cancelar</button>
            <button className="btn btn--primary" type="submit" form={formId} disabled={saving || removed}>Salvar tarefa</button>
          </>
        }
      >
        <form id={formId} className="form-stack" onSubmit={submit} noValidate>
          {fieldsJsx}
        </form>
      </Overlay>
    )
  }

  return (
    <div className="layer fullscreen" role="dialog" aria-modal="true" aria-labelledby="task-form-title">
      <header className="appbar appbar--form">
        <button className="iconbtn" type="button" aria-label="Fechar sem salvar" onClick={onCancel}>
          <CloseIcon />
        </button>
        <h1 className="appbar__heading" id="task-form-title">{heading}</h1>
      </header>
      <form id={formId} className="scroll form-stack" onSubmit={submit} noValidate>
        {fieldsJsx}
      </form>
      <div className="bottombar">
        <button className="btn btn--secondary" type="button" onClick={onCancel}>Cancelar</button>
        <button className="btn btn--primary" type="submit" form={formId} disabled={saving || removed}>Salvar tarefa</button>
      </div>
    </div>
  )
}
