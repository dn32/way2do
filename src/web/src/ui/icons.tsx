import type { ReactNode } from 'react'

// Ícones de traço do design aprovado (docs/marca.html, seção 7): SVG embutido, sem biblioteca, cor do texto.

function Icon({ size, children }: { size?: 'sm' | 'lg'; children: ReactNode }) {
  return (
    <svg className={size ? `ico ico-${size}` : 'ico'} viewBox="0 0 24 24" aria-hidden="true">
      {children}
    </svg>
  )
}

export const CheckIcon = ({ size }: { size?: 'sm' | 'lg' }) => (
  <Icon size={size}>
    <polyline points="20 6 9 17 4 12" />
  </Icon>
)

export const PlusIcon = () => (
  <Icon>
    <line x1="12" y1="5" x2="12" y2="19" />
    <line x1="5" y1="12" x2="19" y2="12" />
  </Icon>
)

export const CloseIcon = () => (
  <Icon>
    <line x1="18" y1="6" x2="6" y2="18" />
    <line x1="6" y1="6" x2="18" y2="18" />
  </Icon>
)

export const MenuIcon = () => (
  <Icon>
    <line x1="3" y1="6" x2="21" y2="6" />
    <line x1="3" y1="12" x2="21" y2="12" />
    <line x1="3" y1="18" x2="21" y2="18" />
  </Icon>
)

export const FilterIcon = () => (
  <Icon>
    <polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3" />
  </Icon>
)

export const TagIcon = ({ size }: { size?: 'sm' }) => (
  <Icon size={size}>
    <path d="M20.59 13.41l-7.17 7.17a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z" />
    <line x1="7" y1="7" x2="7.01" y2="7" />
  </Icon>
)

export const SearchIcon = () => (
  <Icon>
    <circle cx="11" cy="11" r="8" />
    <line x1="21" y1="21" x2="16.65" y2="16.65" />
  </Icon>
)

export const CalendarIcon = () => (
  <Icon size="sm">
    <rect x="3" y="4" width="18" height="18" rx="2" />
    <line x1="16" y1="2" x2="16" y2="6" />
    <line x1="8" y1="2" x2="8" y2="6" />
    <line x1="3" y1="10" x2="21" y2="10" />
  </Icon>
)

export const AlertIcon = ({ size }: { size?: 'sm' | 'lg' }) => (
  <Icon size={size}>
    <path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z" />
    <line x1="12" y1="9" x2="12" y2="13" />
    <line x1="12" y1="17" x2="12.01" y2="17" />
  </Icon>
)

export const InfoIcon = ({ size }: { size?: 'sm' }) => (
  <Icon size={size}>
    <circle cx="12" cy="12" r="10" />
    <line x1="12" y1="16" x2="12" y2="12" />
    <line x1="12" y1="8" x2="12.01" y2="8" />
  </Icon>
)

export const TrashIcon = ({ size }: { size?: 'lg' }) => (
  <Icon size={size}>
    <polyline points="3 6 5 6 21 6" />
    <path d="M19 6l-1 14H6L5 6" />
    <path d="M10 11v6M14 11v6" />
  </Icon>
)

export const LogOutIcon = () => (
  <Icon>
    <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4" />
    <polyline points="16 17 21 12 16 7" />
    <line x1="21" y1="12" x2="9" y2="12" />
  </Icon>
)

export const ClockIcon = () => (
  <Icon>
    <circle cx="12" cy="12" r="10" />
    <polyline points="12 6 12 12 16 14" />
  </Icon>
)

export const ClipboardIcon = () => (
  <svg className="ico" style={{ width: 40, height: 40 }} viewBox="0 0 24 24" aria-hidden="true">
    <path d="M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2" />
    <rect x="8" y="2" width="8" height="4" rx="1" />
    <polyline points="9 14 11 16 15 12" />
  </svg>
)

/** Barras de prioridade do design: uma, duas ou três barras cheias. */
export function PriorityBars({ level }: { level: 1 | 2 | 3 }) {
  return (
    <svg className="ico-sm" viewBox="0 0 16 16" aria-hidden="true">
      <rect x="1" y="10" width="3.5" height="5" rx="1" fill="currentColor" />
      <rect x="6.25" y="6" width="3.5" height="9" rx="1" fill="currentColor" opacity={level >= 2 ? 1 : 0.25} />
      <rect x="11.5" y="2" width="3.5" height="13" rx="1" fill="currentColor" opacity={level >= 3 ? 1 : 0.25} />
    </svg>
  )
}

// Indicador de sincronização (docs/marca.html, "Indicador de sincronização").
export const WifiOffIcon = () => (
  <Icon>
    <line x1="2" y1="2" x2="22" y2="22" />
    <path d="M8.5 16.5a5 5 0 0 1 7 0M2 8.8a15 15 0 0 1 4.2-2.6M10.7 5.1A15 15 0 0 1 22 8.8M5 12.9a10 10 0 0 1 5.2-2.8M19 12.9a10 10 0 0 0-2-1.5M12 20h.01" />
  </Icon>
)

export const RefreshIcon = () => (
  <Icon>
    <polyline points="23 4 23 10 17 10" />
    <path d="M20.5 15a9 9 0 1 1-2.1-9.4L23 10" />
  </Icon>
)

export const ErrorIcon = () => (
  <Icon>
    <circle cx="12" cy="12" r="10" />
    <line x1="12" y1="8" x2="12" y2="12" />
    <line x1="12" y1="16" x2="12.01" y2="16" />
  </Icon>
)

export const LockIcon = () => (
  <Icon>
    <rect x="3" y="11" width="18" height="11" rx="2" />
    <path d="M7 11V7a5 5 0 0 1 10 0v4" />
  </Icon>
)

// Integrações.
export const LinkIcon = () => (
  <Icon>
    <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
    <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
  </Icon>
)

export const ExternalLinkIcon = () => (
  <Icon>
    <path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" />
    <polyline points="15 3 21 3 21 9" />
    <line x1="10" y1="14" x2="21" y2="3" />
  </Icon>
)
