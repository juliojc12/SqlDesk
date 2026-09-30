import { useEffect, useRef, type ReactNode } from 'react'

interface Props {
  title: string
  children: ReactNode
  onCancel: () => void
  width?: number
  role?: 'dialog' | 'alertdialog'
}

/** Casca de modal: Esc cancela, o foco fica preso dentro e volta ao elemento anterior ao fechar. */
export function Modal({ title, children, onCancel, width = 400, role = 'dialog' }: Props) {
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null
    return () => previous?.focus?.()
  }, [])

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-overlay"
      onKeyDown={(e) => {
        if (e.key === 'Escape') {
          e.stopPropagation()
          onCancel()
        }
        if (e.key === 'Tab') {
          const f = [...(ref.current?.querySelectorAll<HTMLElement>('button:not(:disabled), input, textarea, select, [tabindex="0"]') ?? [])]
          if (f.length === 0) return
          const first = f[0]
          const last = f[f.length - 1]
          if (e.shiftKey && document.activeElement === first) {
            e.preventDefault()
            last.focus()
          } else if (!e.shiftKey && document.activeElement === last) {
            e.preventDefault()
            first.focus()
          }
        }
      }}
    >
      <div ref={ref} role={role} aria-modal aria-label={title} style={{ width }} className="max-h-[90vh] rounded-xl border border-line bg-surface shadow-2xl">
        {children}
      </div>
    </div>
  )
}

export const btnBase = 'rounded-md border border-line px-3 py-1.5 text-sm hover:bg-hover'
export const btnPrimary = 'rounded-md bg-blue-600 px-3 py-1.5 text-sm text-white hover:bg-blue-700'
export const btnDanger = 'rounded-md bg-red-600 px-3 py-1.5 text-sm text-white hover:bg-red-700'
