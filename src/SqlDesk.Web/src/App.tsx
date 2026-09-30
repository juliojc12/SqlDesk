import { useCallback, useEffect, useState } from 'react'
import { BridgeCallError, invoke } from './bridge'
import { ConfirmDialog } from './components/ConfirmDialog'
import { ConnectionDialog, nextDefaultColor } from './components/ConnectionDialog'
import { Sidebar } from './components/Sidebar'
import type { ConnectionInfo } from './contracts'

type Editing = { connection: ConnectionInfo | null } | null

const msg = (e: unknown) => (e instanceof BridgeCallError ? e.detail.message : String(e))

export default function App() {
  const [connections, setConnections] = useState<ConnectionInfo[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [editing, setEditing] = useState<Editing>(null)
  const [deleting, setDeleting] = useState<ConnectionInfo | null>(null)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(async () => {
    try {
      setConnections((await invoke('connections.list', {})).connections)
      setError(null)
    } catch (e) {
      setError(msg(e))
    }
  }, [])

  useEffect(() => {
    void reload()
  }, [reload])

  async function duplicate(c: ConnectionInfo) {
    try {
      const copy = await invoke('connections.duplicate', { id: c.id })
      await reload()
      setSelectedId(copy.id)
    } catch (e) {
      setError(msg(e))
    }
  }

  async function confirmDelete() {
    if (!deleting) return
    try {
      await invoke('connections.delete', { id: deleting.id })
      if (selectedId === deleting.id) setSelectedId(null)
      setDeleting(null)
      await reload()
    } catch (e) {
      setDeleting(null)
      setError(msg(e))
    }
  }

  return (
    <div className="flex h-screen bg-white text-neutral-900 dark:bg-neutral-900 dark:text-neutral-100">
      <Sidebar
        connections={connections}
        selectedId={selectedId}
        onSelect={setSelectedId}
        onNew={() => setEditing({ connection: null })}
        onEdit={(c) => setEditing({ connection: c })}
        onDuplicate={(c) => void duplicate(c)}
        onDelete={setDeleting}
      />
      <main className="flex-1 p-8">
        {error && <p role="alert" className="mb-4 rounded-md bg-red-100 p-3 text-sm text-red-900">{error}</p>}
        <p className="text-neutral-500">Selecione uma conexão ou crie uma nova. As abas de query chegam na próxima fase.</p>
      </main>

      {editing && (
        <ConnectionDialog
          connection={editing.connection}
          defaultColor={nextDefaultColor(connections)}
          onClose={() => setEditing(null)}
          onSaved={async (c) => {
            setEditing(null)
            await reload()
            setSelectedId(c.id)
          }}
        />
      )}
      {deleting && (
        <ConfirmDialog title="Excluir conexão" confirmLabel="Excluir" danger onConfirm={() => void confirmDelete()} onCancel={() => setDeleting(null)}>
          Excluir a conexão <strong>{deleting.name}</strong>? A senha salva também será removida.
        </ConfirmDialog>
      )}
    </div>
  )
}
