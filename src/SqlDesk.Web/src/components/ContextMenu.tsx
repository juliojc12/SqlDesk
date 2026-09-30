import { useEffect, useLayoutEffect, useRef, useState } from 'react'

export type MenuItem =
  | { separator: true }
  | { label: string; onSelect: () => void; disabled?: boolean; danger?: boolean; separator?: false }

interface Props {
  x: number
  y: number
  items: MenuItem[]
  onClose: () => void
}

export function ContextMenu({ x, y, items, onClose }: Props) {
  const ref = useRef<HTMLDivElement>(null)
  const [pos, setPos] = useState({ x, y })

  // Mantém o menu dentro da janela.
  useLayoutEffect(() => {
    const r = ref.current?.getBoundingClientRect()
    if (r) setPos({ x: Math.min(x, window.innerWidth - r.width - 4), y: Math.min(y, window.innerHeight - r.height - 4) })
  }, [x, y])

  useEffect(() => {
    const close = () => onClose()
    const key = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('mousedown', close)
    window.addEventListener('blur', close)
    window.addEventListener('keydown', key)
    return () => {
      window.removeEventListener('mousedown', close)
      window.removeEventListener('blur', close)
      window.removeEventListener('keydown', key)
    }
  }, [onClose])

  return (
    <div
      ref={ref}
      role="menu"
      style={{ left: pos.x, top: pos.y }}
      className="fixed z-50 min-w-44 rounded-lg border border-line bg-surface p-1 shadow-xl"
      onMouseDown={(e) => e.stopPropagation()}
    >
      {items.map((it, i) =>
        it.separator ? (
          <div key={i} className="my-1 h-px bg-line" role="separator" />
        ) : (
          <button
            key={it.label}
            role="menuitem"
            disabled={it.disabled}
            onClick={() => {
              onClose()
              it.onSelect()
            }}
            className={`block w-full rounded px-3 py-1.5 text-left text-sm enabled:hover:bg-hover disabled:opacity-40 ${it.danger ? 'text-danger' : ''}`}
          >
            {it.label}
          </button>
        ),
      )}
    </div>
  )
}
