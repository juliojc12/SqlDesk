import type { ConnectionInfo } from '../contracts'

interface Props {
  connections: ConnectionInfo[]
  selectedId: string | null
  onSelect: (id: string) => void
  onNew: () => void
  onEdit: (c: ConnectionInfo) => void
  onDuplicate: (c: ConnectionInfo) => void
  onDelete: (c: ConnectionInfo) => void
}

const btn = 'rounded px-2 py-1 text-xs hover:bg-neutral-200 disabled:opacity-40 dark:hover:bg-neutral-700'

export function Sidebar({ connections, selectedId, onSelect, onNew, onEdit, onDuplicate, onDelete }: Props) {
  const selected = connections.find((c) => c.id === selectedId) ?? null
  return (
    <aside className="flex w-64 shrink-0 flex-col border-r border-neutral-200 bg-neutral-50 dark:border-neutral-700 dark:bg-neutral-800">
      <div className="flex items-center gap-1 border-b border-neutral-200 p-2 dark:border-neutral-700">
        <span className="mr-auto px-1 text-xs font-semibold uppercase tracking-wide text-neutral-500">Conexões</span>
        <button className={btn} onClick={onNew}>Nova</button>
        <button className={btn} disabled={!selected} onClick={() => selected && onEdit(selected)}>Editar</button>
        <button className={btn} disabled={!selected} onClick={() => selected && onDuplicate(selected)}>Duplicar</button>
        <button className={btn} disabled={!selected} onClick={() => selected && onDelete(selected)}>Excluir</button>
      </div>
      <ul className="flex-1 overflow-y-auto p-1" role="listbox" aria-label="Conexões">
        {connections.length === 0 && <li className="p-3 text-sm text-neutral-500">Nenhuma conexão. Clique em “Nova”.</li>}
        {connections.map((c) => (
          <li
            key={c.id}
            role="option"
            aria-selected={c.id === selectedId}
            onClick={() => onSelect(c.id)}
            onDoubleClick={() => onEdit(c)}
            className={`flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-sm ${c.id === selectedId ? 'bg-blue-100 dark:bg-neutral-700' : 'hover:bg-neutral-100 dark:hover:bg-neutral-700/50'}`}
          >
            <span className="h-2.5 w-2.5 shrink-0 rounded-full" style={{ backgroundColor: c.color }} data-testid="dot" />
            <span className="truncate">{c.name}</span>
            <span className="ml-auto truncate text-xs text-neutral-500">{c.settings.server}</span>
          </li>
        ))}
      </ul>
    </aside>
  )
}
