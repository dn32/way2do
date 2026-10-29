import { useEffect, useRef, type ReactNode } from 'react'
import { CloseIcon } from './icons'

/**
 * Painel que sobe de baixo (celular) ou diálogo centralizado (desktop), como no design. Fecha com Esc e com toque
 * no fundo escurecido; o foco vai para dentro do painel ao abrir.
 */
export function Overlay({
  title,
  desktop,
  onClose,
  children,
  footer,
  small,
}: {
  title: string
  desktop: boolean
  onClose: () => void
  children: ReactNode
  footer?: ReactNode
  small?: boolean
}) {
  const panel = useRef<HTMLElement>(null)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    panel.current?.querySelector<HTMLElement>('input, select, textarea, button:not(.iconbtn)')?.focus()
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const titleId = `overlay-${title.replace(/\W+/g, '-')}`

  if (desktop) {
    return (
      <div className="layer modal" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
        <section ref={panel} className={small ? 'dialog dialog--sm' : 'dialog'} role="dialog" aria-modal="true" aria-labelledby={titleId}>
          <div className="dialog__head">
            <h2 className="dialog__title" id={titleId}>{title}</h2>
            <button className="iconbtn" type="button" aria-label="Fechar" onClick={onClose}><CloseIcon /></button>
          </div>
          <div className="dialog__body">{children}</div>
          {footer && <div className="dialog__foot">{footer}</div>}
        </section>
      </div>
    )
  }

  return (
    <div className="layer">
      <div className="scrim" onClick={onClose} />
      <section ref={panel} className="sheet" role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <div className="sheet__grip" />
        <div className="sheet__head">
          <h2 className="sheet__title" id={titleId}>{title}</h2>
          <button className="iconbtn" type="button" aria-label="Fechar" onClick={onClose}><CloseIcon /></button>
        </div>
        {children}
        {footer && <div className="sheet__foot">{footer}</div>}
      </section>
    </div>
  )
}
