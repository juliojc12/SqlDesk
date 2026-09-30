import { useEffect, useRef, type ReactNode } from 'react'
import { btnBase, btnDanger, btnPrimary, Modal } from './Modal'

interface Props {
  title: string
  children: ReactNode
  confirmLabel: string
  danger?: boolean
  onConfirm: () => void
  onCancel: () => void
}

/** Confirmação: o botão seguro (Cancelar) recebe o foco, para que um Enter distraído não confirme. */
export function ConfirmDialog({ title, children, confirmLabel, danger, onConfirm, onCancel }: Props) {
  const cancelRef = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    cancelRef.current?.focus()
  }, [])
  return (
    <Modal title={title} onCancel={onCancel} role="alertdialog">
      <div className="p-5">
        <h2 className="text-base font-semibold">{title}</h2>
        <div className="mt-2 text-sm text-muted">{children}</div>
        <div className="mt-5 flex justify-end gap-2">
          <button ref={cancelRef} onClick={onCancel} className={btnBase}>Cancelar</button>
          <button onClick={onConfirm} className={danger ? btnDanger : btnPrimary}>{confirmLabel}</button>
        </div>
      </div>
    </Modal>
  )
}
