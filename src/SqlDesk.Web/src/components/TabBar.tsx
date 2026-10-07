import { useEffect, useRef, useState } from 'react'
import { NEUTRAL_COLOR } from '../colors'
import type { ConnectionInfo } from '../contracts'
import { isDirty, type Tab } from '../tabsState'

interface Props {
  tabs: Tab[]
  activeId: string | null
  connections: ConnectionInfo[]
  onActivate: (id: string) => void
  onClose: (id: string) => void
  onRename: (id: string, title: string) => void
  /** Solta a aba arrastada na posição `toIndex` da lista. */
  onMove: (id: string, toIndex: number) => void
  onNew: () => void
  /** Abas com transação aberta (selo TRAN). */
  tranTabs: ReadonlySet<string>
}

export function TabBar({ tabs, activeId, connections, onActivate, onClose, onRename, onMove, onNew, tranTabs }: Props) {
  const [renaming, setRenaming] = useState<string | null>(null)
  const activeRef = useRef<HTMLDivElement>(null)
  const dragId = useRef<string | null>(null)
  const [dropAt, setDropAt] = useState<string | null>(null)

  // Mantém a aba ativa visível quando há rolagem horizontal.
  useEffect(() => {
    activeRef.current?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
  }, [activeId, tabs.length])

  return (
    <div className="flex h-10 shrink-0 items-stretch border-b border-line bg-sidebar">
      <div role="tablist" aria-label="Abas de query" className="thin-scroll flex min-w-0 overflow-x-auto overflow-y-hidden">
        {tabs.map((t) => {
          const color = connections.find((c) => c.id === t.connectionId)?.color ?? NEUTRAL_COLOR
          const active = t.id === activeId
          const dropTarget = dropAt === t.id
          return (
            <div
              key={t.id}
              ref={active ? activeRef : undefined}
              role="tab"
              aria-selected={active}
              tabIndex={active ? 0 : -1}
              // Arrastar uma aba para outra a reposiciona (mesmo gesto das colunas da grade).
              draggable={renaming !== t.id}
              onDragStart={(e) => {
                dragId.current = t.id
                e.dataTransfer.effectAllowed = 'move'
              }}
              onDragOver={(e) => {
                if (dragId.current === null) return
                e.preventDefault()
                setDropAt(dragId.current === t.id ? null : t.id)
              }}
              onDragLeave={() => setDropAt((cur) => (cur === t.id ? null : cur))}
              onDrop={(e) => {
                e.preventDefault()
                if (dragId.current !== null) onMove(dragId.current, tabs.findIndex((x) => x.id === t.id))
                dragId.current = null
                setDropAt(null)
              }}
              onDragEnd={() => {
                dragId.current = null
                setDropAt(null)
              }}
              onClick={() => onActivate(t.id)}
              onAuxClick={(e) => e.button === 1 && onClose(t.id)}
              onDoubleClick={() => setRenaming(t.id)}
              title={t.filePath ?? t.title}
              style={{
                borderTopColor: color,
                boxShadow: dropTarget ? 'inset 3px 0 0 var(--fg)' : undefined,
                backgroundColor: active ? `color-mix(in srgb, ${color} 10%, var(--surface))` : undefined,
              }}
              className={`group flex max-w-56 shrink-0 cursor-pointer items-center gap-2 border-t-[3px] px-3 text-sm ${active ? 'text-fg' : 'text-muted hover:bg-hover'}`}
            >
              <span className="h-2 w-2 shrink-0 rounded-full" style={{ backgroundColor: color }} aria-hidden />
              {renaming === t.id ? (
                <RenameInput
                  initial={t.title}
                  onDone={(v) => {
                    setRenaming(null)
                    if (v !== null) onRename(t.id, v)
                  }}
                />
              ) : (
                <span className="truncate">{t.title}</span>
              )}
              {tranTabs.has(t.id) && (
                <span className="shrink-0 rounded bg-amber-500 px-1 text-[10px] font-bold leading-4 text-black" title="Transação aberta nesta aba" aria-label="Transação aberta">TRAN</span>
              )}
              {isDirty(t) && <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-fg" title="Alterações não salvas" aria-label="Alterações não salvas" />}
              <button
                aria-label={`Fechar ${t.title}`}
                className="icon ml-1 flex h-5 w-5 shrink-0 items-center justify-center rounded text-[10px] opacity-0 hover:bg-selected group-hover:opacity-100 group-aria-selected:opacity-100"
                onClick={(e) => {
                  e.stopPropagation()
                  onClose(t.id)
                }}
              >
                &#xE8BB;
              </button>
            </div>
          )
        })}
      </div>
      <button className="icon mx-1 my-auto flex h-8 w-8 shrink-0 items-center justify-center rounded text-fg hover:bg-hover" title="Nova aba (Ctrl+T)" aria-label="Nova aba" onClick={onNew}>
        &#xE710;
      </button>
    </div>
  )
}

function RenameInput({ initial, onDone }: { initial: string; onDone: (value: string | null) => void }) {
  const ref = useRef<HTMLInputElement>(null)
  useEffect(() => {
    ref.current?.select()
  }, [])
  return (
    <input
      ref={ref}
      defaultValue={initial}
      aria-label="Nome da aba"
      className="w-32 rounded border border-line bg-input px-1 text-sm text-fg"
      onClick={(e) => e.stopPropagation()}
      onDoubleClick={(e) => e.stopPropagation()}
      onBlur={(e) => onDone(e.currentTarget.value)}
      onKeyDown={(e) => {
        e.stopPropagation()
        if (e.key === 'Enter') onDone(e.currentTarget.value)
        if (e.key === 'Escape') onDone(null)
      }}
    />
  )
}
