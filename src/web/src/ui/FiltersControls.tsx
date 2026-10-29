import type { Priority } from '../domain/types'
import type { Filters } from './filters'
import { PRIORITY_LABEL } from './format'
import { SearchIcon } from './icons'

const STATUS_OPTIONS: { value: Filters['status']; label: string }[] = [
  { value: 'all', label: 'Todas' },
  { value: 'pending', label: 'Pendentes' },
  { value: 'done', label: 'Concluídas' },
]
const PRIORITY_OPTIONS: Filters['priority'][] = ['all', 'high', 'medium', 'low']

function priorityLabel(p: Filters['priority']): string {
  return p === 'all' ? 'Todas' : PRIORITY_LABEL[p as Priority]
}

export function SearchField({ value, onChange, hint }: { value: string; onChange: (v: string) => void; hint?: boolean }) {
  return (
    <div className="field field--grow">
      <label className="label" htmlFor="search">Buscar</label>
      <span className="search">
        <SearchIcon />
        <input className="input" id="search" type="search" placeholder="Título ou descrição" value={value} onChange={(e) => onChange(e.target.value)} />
      </span>
      {hint && <span className="hint">Ignora maiúsculas e acentos: “galpao” acha “galpão”.</span>}
    </div>
  )
}

export function StatusField({ value, onChange }: { value: Filters['status']; onChange: (v: Filters['status']) => void }) {
  return (
    <div className="field">
      <span className="label" id="filter-status">Status</span>
      <div className="seg" role="group" aria-labelledby="filter-status">
        {STATUS_OPTIONS.map((o) => (
          <button key={o.value} type="button" className={o.value === value ? 'seg__opt seg__opt--on' : 'seg__opt'} aria-pressed={o.value === value} onClick={() => onChange(o.value)}>
            {o.label}
          </button>
        ))}
      </div>
    </div>
  )
}

/** Prioridade: botões no celular (painel), lista suspensa no desktop (barra de filtros). */
export function PriorityField({ value, onChange, compact }: { value: Filters['priority']; onChange: (v: Filters['priority']) => void; compact?: boolean }) {
  if (compact) {
    return (
      <div className="field">
        <label className="label" htmlFor="filter-priority">Prioridade</label>
        <select className="input" id="filter-priority" value={value} onChange={(e) => onChange(e.target.value as Filters['priority'])}>
          {PRIORITY_OPTIONS.map((p) => (
            <option key={p} value={p}>{priorityLabel(p)}</option>
          ))}
        </select>
      </div>
    )
  }
  return (
    <div className="field">
      <span className="label" id="filter-priority">Prioridade</span>
      <div className="seg" role="group" aria-labelledby="filter-priority">
        {PRIORITY_OPTIONS.map((p) => (
          <button key={p} type="button" className={p === value ? 'seg__opt seg__opt--on' : 'seg__opt'} aria-pressed={p === value} onClick={() => onChange(p)}>
            {priorityLabel(p)}
          </button>
        ))}
      </div>
    </div>
  )
}

export function TagField({ value, tags, onChange }: { value: string | null; tags: { name: string; count: number }[]; onChange: (v: string | null) => void }) {
  return (
    <div className="field">
      <label className="label" htmlFor="filter-tag">Tag</label>
      <select className="input" id="filter-tag" value={value ?? ''} onChange={(e) => onChange(e.target.value || null)}>
        <option value="">Todas as tags</option>
        {tags.map((t) => (
          <option key={t.name} value={t.name}>{`${t.name} (${t.count})`}</option>
        ))}
      </select>
    </div>
  )
}
