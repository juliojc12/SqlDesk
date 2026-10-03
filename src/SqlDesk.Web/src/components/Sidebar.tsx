import { useState } from 'react'
import type { ConnectionInfo } from '../contracts'
import { useMeta } from '../metadataStore'
import type { MetaObject } from '../metadataIndex'
import { providerOf } from '../providers'
import { ContextMenu, type MenuItem } from './ContextMenu'
import { ObjectTree } from './ObjectTree'

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
  /** Expandir a conexão pela primeira vez: o app carrega os metadados se ainda não há cache. */
  onExpand: (c: ConnectionInfo) => void
  onRefreshMetadata: (c: ConnectionInfo) => void
  onOpenObject: (c: ConnectionInfo, o: MetaObject) => void
}

function ConnectionTree({ conn, onOpenObject }: { conn: ConnectionInfo; onOpenObject: Props['onOpenObject'] }) {
  const meta = useMeta(conn.id)
  return <ObjectTree index={meta.index} loading={meta.loading} error={meta.error} onOpen={(o) => onOpenObject(conn, o)} />
}

export function Sidebar({ connections, selectedId, connectedIds, width, onSelect, onNew, onNewQuery, onEdit, onDuplicate, onDisconnect, onDelete, onExpand, onRefreshMetadata, onOpenObject }: Props) {
  const [menu, setMenu] = useState<{ x: number; y: number; conn: ConnectionInfo } | null>(null)
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(new Set())

  const toggleExpanded = (c: ConnectionInfo) =>
    setExpanded((cur) => {
      const next = new Set(cur)
      if (next.delete(c.id)) return next
      next.add(c.id)
      onExpand(c)
      return next
    })

  const items = (c: ConnectionInfo): MenuItem[] => [
    { label: 'Nova query', onSelect: () => onNewQuery(c) },
    { label: 'Atualizar metadados', onSelect: () => onRefreshMetadata(c) },
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
          <li key={c.id} role="option" aria-selected={c.id === selectedId}>
            <div
              onClick={() => onSelect(c.id)}
              onDoubleClick={() => onNewQuery(c)}
              onContextMenu={(e) => {
                e.preventDefault()
                onSelect(c.id)
                setMenu({ x: e.clientX, y: e.clientY, conn: c })
              }}
              title={`${providerOf(c.settings).name} · ${c.settings.server} · ${c.settings.database}${connectedIds.has(c.id) ? ' (conectado)' : ''}`}
              className={`flex cursor-pointer items-center gap-2 rounded-lg py-2 pl-1 pr-3 text-[15px] ${c.id === selectedId ? 'bg-selected' : 'hover:bg-hover'}`}
            >
              <button
                className="icon flex h-5 w-5 shrink-0 items-center justify-center rounded text-[10px] text-muted hover:bg-selected"
                aria-label={expanded.has(c.id) ? `Recolher ${c.name}` : `Expandir ${c.name}`}
                aria-expanded={expanded.has(c.id)}
                onClick={(e) => {
                  e.stopPropagation()
                  toggleExpanded(c)
                }}
                onDoubleClick={(e) => e.stopPropagation()}
              >
                {expanded.has(c.id) ? '' : ''}
              </button>
              <span
                className="h-2.5 w-2.5 shrink-0 rounded-full"
                style={{ backgroundColor: c.color, boxShadow: connectedIds.has(c.id) ? `0 0 0 3px color-mix(in srgb, ${c.color} 30%, transparent)` : undefined }}
                data-testid="dot"
              />
              <span className="min-w-0 flex-1 truncate">{c.name}</span>
              <span className="shrink-0 text-xs text-muted" data-testid="provider">{providerOf(c.settings).name}</span>
            </div>
            {expanded.has(c.id) && <ConnectionTree conn={c} onOpenObject={onOpenObject} />}
          </li>
        ))}
      </ul>
      {menu && <ContextMenu x={menu.x} y={menu.y} items={items(menu.conn)} onClose={() => setMenu(null)} />}
    </aside>
  )
}
