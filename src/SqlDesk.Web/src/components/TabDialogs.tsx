import { useEffect, useRef, useState } from 'react'
import type { ConnectionInfo } from '../contracts'
import { btnBase, btnDanger, btnPrimary, Modal } from './Modal'

/** Escolha de conexão (nova aba sem contexto, abrir .sql, aba sem conexão). */
export function ConnectionPicker({
  connections,
  title,
  initialId,
  onPick,
  onCancel,
}: {
  connections: ConnectionInfo[]
  title: string
  initialId?: string | null
  onPick: (c: ConnectionInfo) => void
  onCancel: () => void
}) {
  const [sel, setSel] = useState(initialId ?? connections[0]?.id ?? null)
  const chosen = connections.find((c) => c.id === sel) ?? null
  return (
    <Modal title={title} onCancel={onCancel} width={420}>
      <div className="p-5">
        <h2 className="text-base font-semibold">{title}</h2>
        {connections.length === 0 ? (
          <p className="mt-3 text-sm text-muted">Crie uma conexão primeiro (botão “+” no painel lateral).</p>
        ) : (
          <ul role="listbox" aria-label="Conexões" className="thin-scroll mt-3 max-h-64 overflow-y-auto">
            {connections.map((c) => (
              <li
                key={c.id}
                role="option"
                aria-selected={c.id === sel}
                tabIndex={0}
                autoFocus={c.id === sel}
                onClick={() => setSel(c.id)}
                onDoubleClick={() => onPick(c)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') onPick(c)
                  if (e.key === ' ') setSel(c.id)
                }}
                className={`flex cursor-pointer items-center gap-3 rounded-lg px-3 py-2 ${c.id === sel ? 'bg-selected' : 'hover:bg-hover'}`}
              >
                <span className="h-2.5 w-2.5 rounded-full" style={{ backgroundColor: c.color }} />
                {c.name}
                <span className="ml-auto truncate text-xs text-muted">{c.settings.server}</span>
              </li>
            ))}
          </ul>
        )}
        <div className="mt-5 flex justify-end gap-2">
          <button onClick={onCancel} className={btnBase}>Cancelar</button>
          <button disabled={!chosen} onClick={() => chosen && onPick(chosen)} className={`${btnPrimary} disabled:opacity-40`}>OK</button>
        </div>
      </div>
    </Modal>
  )
}

/** Senha para conexões sem senha salva. Fica só em memória. */
export function PasswordPrompt({ connectionName, error, onSubmit, onCancel }: { connectionName: string; error?: string; onSubmit: (p: string) => void; onCancel: () => void }) {
  const [value, setValue] = useState('')
  const ref = useRef<HTMLInputElement>(null)
  useEffect(() => {
    ref.current?.focus()
  }, [])
  return (
    <Modal title="Senha necessária" onCancel={onCancel}>
      <form
        className="p-5"
        onSubmit={(e) => {
          e.preventDefault()
          onSubmit(value)
        }}
      >
        <h2 className="text-base font-semibold">Senha necessária</h2>
        <p className="mt-2 text-sm text-muted">
          A conexão <strong className="text-fg">{connectionName}</strong> não tem senha salva. A senha informada vale só até fechar o aplicativo.
        </p>
        <input
          ref={ref}
          type="password"
          aria-label="Senha"
          autoComplete="off"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          className="mt-3 w-full rounded-md border border-line bg-input px-2 py-1.5 text-sm"
        />
        {error && <p role="alert" className="mt-2 text-xs text-danger">{error}</p>}
        <div className="mt-5 flex justify-end gap-2">
          <button type="button" onClick={onCancel} className={btnBase}>Cancelar</button>
          <button type="submit" className={btnPrimary}>Conectar</button>
        </div>
      </form>
    </Modal>
  )
}

/** Fechar aba com alterações não salvas. A opção segura (Cancelar) tem o foco. */
export function UnsavedDialog({ title, onSave, onDiscard, onCancel }: { title: string; onSave: () => void; onDiscard: () => void; onCancel: () => void }) {
  const ref = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    ref.current?.focus()
  }, [])
  return (
    <Modal title="Alterações não salvas" onCancel={onCancel} role="alertdialog">
      <div className="p-5">
        <h2 className="text-base font-semibold">Alterações não salvas</h2>
        <p className="mt-2 text-sm text-muted">
          A aba <strong className="text-fg">{title}</strong> tem alterações que ainda não foram salvas em arquivo.
        </p>
        <div className="mt-5 flex justify-end gap-2">
          <button ref={ref} onClick={onCancel} className={btnBase}>Cancelar</button>
          <button onClick={onDiscard} className={btnDanger}>Descartar</button>
          <button onClick={onSave} className={btnPrimary}>Salvar…</button>
        </div>
      </div>
    </Modal>
  )
}
