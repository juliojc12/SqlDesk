import { useState } from 'react'
import type { ConnectionInfo } from '../contracts'
import { ContextMenu, type MenuItem } from './ContextMenu'

interface Props {
  connections: ConnectionInfo[]
  selectedId: string | null
  connectedIds: ReadonlySet<string>
  width: number
  onSelect: (id: string) => void
  onNew: () => void
  onNewQuery: (c: ConnectionInfo) => void
  onEdit: (c: ConnectionInfo) => void
  onDuplicate: (c: ConnectionInfo) => void
  onDisconnect: (c: ConnectionInfo) => void
  onDelete: (c: ConnectionInfo) => void
}

export function Sidebar({ connections, selectedId, connectedIds, width, onSelect, onNew, onNewQuery, onEdit, onDuplicate, onDisconnect, onDelete }: Props) {
  const [menu, setMenu] = useState<{ x: number; y: number; conn: ConnectionInfo } | null>(null)

  const items = (c: ConnectionInfo): MenuItem[] => [
    { label: 'Nova query', onSelect: () => onNewQuery(c) },
    { label: 'Desconectar', disabled: !connectedIds.has(c.id), onSelect: () => onDisconnect(c) },
    { separator: true },
    { label: 'Editar…', onSelect: () => onEdit(c) },
    { label: 'Duplicar', onSelect: () => onDuplicate(c) },
    { separator: true },
    { label: 'Excluir…', danger: true, onSelect: () => onDelete(c) },
  ]

  return (
    <aside style={{ width }} className="flex shrink-0 flex-col border-r border-line bg-sidebar" aria-label="Conexões">
      <div className="flex h-10 items-center justify-between px-4">
        <span className="text-sm text-muted">Conexões</span>
        <button className="icon flex h-7 w-7 items-center justify-center rounded text-fg hover:bg-hover" title="Nova conexão" aria-label="Nova conexão" onClick={onNew}>
          &#xE710;
        </button>
      </div>
      <ul className="thin-scroll flex-1 overflow-y-auto px-2 pb-2" role="listbox" aria-label="Lista de conexões">
        {connections.length === 0 && <li className="px-3 py-2 text-sm text-muted">Nenhuma conexão. Use “+” para criar.</li>}
        {connections.map((c) => (
          <li
            key={c.id}
            role="option"
            aria-selected={c.id === selectedId}
            onClick={() => onSelect(c.id)}
            onDoubleClick={() => onNewQuery(c)}
            onContextMenu={(e) => {
              e.preventDefault()
              onSelect(c.id)
              setMenu({ x: e.clientX, y: e.clientY, conn: c })
            }}
            className={`flex cursor-pointer items-center gap-3 rounded-lg px-3 py-2 text-[15px] ${c.id === selectedId ? 'bg-selected' : 'hover:bg-hover'}`}
            title={`${c.settings.server} · ${c.settings.database}${connectedIds.has(c.id) ? ' (conectado)' : ''}`}
          >
            <span
              className="h-2.5 w-2.5 shrink-0 rounded-full"
              style={{ backgroundColor: c.color, boxShadow: connectedIds.has(c.id) ? `0 0 0 3px color-mix(in srgb, ${c.color} 30%, transparent)` : undefined }}
              data-testid="dot"
            />
            <span className="truncate">{c.name}</span>
          </li>
        ))}
      </ul>
      {menu && <ContextMenu x={menu.x} y={menu.y} items={items(menu.conn)} onClose={() => setMenu(null)} />}
    </aside>
  )
}
