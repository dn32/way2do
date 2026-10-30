import type { Filters } from './filters'
import { PriorityField, SearchField, StatusField, TagField } from './FiltersControls'
import { CheckIcon, LinkIcon, LogOutIcon, TrashIcon } from './icons'
import { Overlay } from './Overlay'
import { TagList } from './TaskList'

// Diálogos e painéis da tela de tarefas. Só recebem dados e callbacks: o estado fica no TasksScreen.

export function DeleteTaskDialog({ title, desktop, onCancel, onConfirm }: { title: string | undefined; desktop: boolean; onCancel: () => void; onConfirm: () => void }) {
  return (
    <Overlay
      title="Excluir esta tarefa?"
      desktop={desktop}
      small
      onClose={onCancel}
      footer={
        <>
          <button className="btn btn--secondary" type="button" onClick={onCancel}>Cancelar</button>
          <button className="btn btn--danger" type="button" onClick={onConfirm}><TrashIcon />Excluir tarefa</button>
        </>
      }
    >
      <p>
        <strong>{title}</strong> será apagada deste aparelho e dos outros onde você usa o Way2Do. Não dá para desfazer.
      </p>
    </Overlay>
  )
}

/** Sair com alterações na fila: elas ficam no aparelho e vão no próximo login (escopo, seção 5). */
export function LogoutDialog({ pending, desktop, onCancel, onConfirm }: { pending: number; desktop: boolean; onCancel: () => void; onConfirm: () => void }) {
  return (
    <Overlay
      title="Sair com alterações pendentes?"
      desktop={desktop}
      small
      onClose={onCancel}
      footer={
        <>
          <button className="btn btn--secondary" type="button" onClick={onCancel}>Cancelar</button>
          <button className="btn btn--danger" type="button" onClick={onConfirm}>Sair mesmo assim</button>
        </>
      }
    >
      <p>
        Há <strong>{pending} {pending === 1 ? 'alteração não sincronizada' : 'alterações não sincronizadas'}</strong> neste aparelho.
        Elas ficam guardadas e serão enviadas no seu próximo login.
      </p>
    </Overlay>
  )
}

export type View = 'tasks' | 'integrations'

/** Menu da conta no celular: navegação entre Tarefas e Integrações, e Sair. */
export function AccountMenu({ userName, view, onNavigate, onLogout, onClose }: { userName: string; view: View; onNavigate: (v: View) => void; onLogout: () => void; onClose: () => void }) {
  return (
    <Overlay title={userName} desktop={false} onClose={onClose}>
      <ul className="menu menu--flat">
        <li>
          <button className="menu__item" type="button" aria-current={view === 'tasks' ? 'page' : undefined} onClick={() => onNavigate('tasks')}><CheckIcon />Tarefas</button>
        </li>
        <li>
          <button className="menu__item" type="button" aria-current={view === 'integrations' ? 'page' : undefined} onClick={() => onNavigate('integrations')}><LinkIcon />Integrações</button>
        </li>
        <li>
          <button className="menu__item menu__item--danger" type="button" onClick={onLogout}><LogOutIcon />Sair</button>
        </li>
      </ul>
    </Overlay>
  )
}

/** Filtros e busca no celular (no desktop ficam na barra acima da lista). */
export function FiltersSheet({ filters, tags, visibleCount, onChange, onClear, onClose }: { filters: Filters; tags: { name: string; count: number }[]; visibleCount: number; onChange: (patch: Partial<Filters>) => void; onClear: () => void; onClose: () => void }) {
  return (
    <Overlay
      title="Filtros"
      desktop={false}
      onClose={onClose}
      footer={
        <>
          <button className="btn btn--secondary" type="button" onClick={onClear}>Limpar</button>
          <button className="btn btn--primary" type="button" onClick={onClose}>Ver {visibleCount} {visibleCount === 1 ? 'tarefa' : 'tarefas'}</button>
        </>
      }
    >
      <SearchField hint value={filters.query} onChange={(query) => onChange({ query })} />
      <StatusField value={filters.status} onChange={(status) => onChange({ status })} />
      <PriorityField value={filters.priority} onChange={(priority) => onChange({ priority })} />
      <TagField value={filters.tag} tags={tags} onChange={(tag) => onChange({ tag })} />
    </Overlay>
  )
}

/** Tags no celular: escolher uma filtra a lista e fecha o painel. */
export function TagsSheet({ tags, total, selected, onSelect, onClose }: { tags: { name: string; count: number }[]; total: number; selected: string | null; onSelect: (tag: string | null) => void; onClose: () => void }) {
  return (
    <Overlay title="Tags" desktop={false} onClose={onClose}>
      <TagList tags={tags} total={total} selected={selected} onSelect={onSelect} />
    </Overlay>
  )
}
