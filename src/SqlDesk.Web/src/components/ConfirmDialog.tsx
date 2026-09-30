import { useEffect, useRef, type ReactNode } from 'react'

interface Props {
  title: string
  children: ReactNode
  confirmLabel: string
  danger?: boolean
  onConfirm: () => void
  onCancel: () => void
}

/** Modal de confirmação: o botão seguro (Cancelar) recebe o foco, para que um Enter distraído não confirme. */
export function ConfirmDialog({ title, children, confirmLabel, danger, onConfirm, onCancel }: Props) {
  const cancelRef = useRef<HTMLButtonElement>(null)
  useEffect(() => cancelRef.current?.focus(), [])
  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40"
      onKeyDown={(e) => e.key === 'Escape' && onCancel()}
    >
      <div role="alertdialog" aria-modal className="w-96 rounded-lg border border-neutral-300 bg-white p-5 shadow-xl dark:border-neutral-700 dark:bg-neutral-800">
        <h2 className="text-base font-semibold">{title}</h2>
        <div className="mt-2 text-sm text-neutral-600 dark:text-neutral-300">{children}</div>
        <div className="mt-5 flex justify-end gap-2">
          <button ref={cancelRef} onClick={onCancel} className="rounded-md border border-neutral-300 px-3 py-1.5 text-sm dark:border-neutral-600">
            Cancelar
          </button>
          <button
            onClick={onConfirm}
            className={`rounded-md px-3 py-1.5 text-sm text-white ${danger ? 'bg-red-600 hover:bg-red-700' : 'bg-blue-600 hover:bg-blue-700'}`}
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  )
}
