import { useState } from 'react'
import type { MetaIndex, MetaObject, ObjectType } from '../metadataIndex'

const GROUPS: { type: ObjectType; label: string; icon: string }[] = [
  { type: 'table', label: 'Tabelas', icon: '' },
  { type: 'view', label: 'Views', icon: '' },
  { type: 'procedure', label: 'Procedures', icon: '' },
  { type: 'function', label: 'Funções', icon: '' },
]

/** Itens por grupo exibidos de início; o resto aparece sob demanda (bancos grandes têm milhares de tabelas). */
const PAGE = 300

interface Props {
  index: MetaIndex | null
  loading: boolean
  error?: string
  onOpen: (o: MetaObject) => void
}

/** Schemas, tabelas, views, procedures e funções de uma conexão, a partir do cache de metadados. */
export function ObjectTree({ index, loading, error, onOpen }: Props) {
  const [open, setOpen] = useState<ReadonlySet<string>>(new Set())
  const [shown, setShown] = useState<Record<string, number>>({})
  const toggle = (key: string) =>
    setOpen((cur) => {
      const next = new Set(cur)
      if (!next.delete(key)) next.add(key)
      return next
    })

  const row = 'flex w-full items-center gap-2 rounded px-2 py-1 text-left text-sm hover:bg-hover'

  if (error) return <p role="alert" className="px-3 py-1 text-xs text-danger">{error}</p>
  if (!index) return <p className="px-3 py-1 text-xs text-muted">{loading ? 'Carregando metadados…' : 'Metadados não carregados.'}</p>
  if (index.schemas.length === 0) return <p className="px-3 py-1 text-xs text-muted">Nenhum objeto visível neste banco.</p>

  return (
    <div role="tree" className="pb-1 pl-3">
      {loading && <p className="px-2 pb-1 text-xs text-muted">Carregando colunas…</p>}
      {index.schemas.map((schema) => {
        const sKey = `s:${schema}`
        const objects = index.objectsOfSchema(schema)
        return (
          <div key={schema} role="treeitem" aria-expanded={open.has(sKey)}>
            <button className={row} onClick={() => toggle(sKey)}>
              <span className="icon w-3 text-[10px] text-muted">{open.has(sKey) ? '' : ''}</span>
              <span className="truncate">{schema}</span>
              <span className="ml-auto text-xs text-muted">{objects.length}</span>
            </button>
            {open.has(sKey) && (
              <div className="pl-3">
                {GROUPS.map((g) => {
                  const items = objects.filter((o) => o.type === g.type)
                  if (items.length === 0) return null
                  const gKey = `${sKey}:${g.type}`
                  const limit = shown[gKey] ?? PAGE
                  return (
                    <div key={g.type} role="group">
                      <button className={row} onClick={() => toggle(gKey)}>
                        <span className="icon w-3 text-[10px] text-muted">{open.has(gKey) ? '' : ''}</span>
                        <span>{g.label}</span>
                        <span className="ml-auto text-xs text-muted">{items.length}</span>
                      </button>
                      {open.has(gKey) && (
                        <ul className="pl-5">
                          {items.slice(0, limit).map((o) => {
                            const oKey = `${gKey}:o:${o.name}`
                            const hasColumns = o.type !== 'procedure'
                            const expanded = open.has(oKey)
                            return (
                              <li key={o.name} role="treeitem" aria-expanded={hasColumns ? expanded : undefined}>
                                <div
                                  title={o.type === 'procedure' ? 'Duplo clique: EXEC' : 'Duplo clique: SELECT TOP 100 em uma nova aba'}
                                  className="flex cursor-pointer items-center gap-1 rounded px-1 py-0.5 text-sm hover:bg-hover"
                                  onDoubleClick={() => onOpen(o)}
                                >
                                  {hasColumns ? (
                                    <button
                                      className="icon flex h-4 w-4 shrink-0 items-center justify-center rounded text-[9px] text-muted hover:bg-selected"
                                      aria-label={`${expanded ? 'Recolher' : 'Mostrar'} as colunas de ${o.name}`}
                                      onClick={() => toggle(oKey)}
                                      onDoubleClick={(e) => e.stopPropagation()}
                                    >
                                      {expanded ? '\uE70D' : '\uE76C'}
                                    </button>
                                  ) : (
                                    <span className="w-4 shrink-0" />
                                  )}
                                  <span className="icon text-xs text-muted">{g.icon}</span>
                                  <span className="truncate">{o.name}</span>
                                </div>
                                {expanded && (
                                  <ul role="group" aria-label={`Colunas de ${o.name}`} className="pb-1 pl-8">
                                    {!index.columnsLoaded ? (
                                      <li className="px-1 text-xs text-muted">Carregando colunas…</li>
                                    ) : (
                                      index.columnsOf(o).map((c) => (
                                        <li key={c.name} role="treeitem" className="flex items-baseline gap-2 px-1 py-px text-xs" title={`${c.name} ${c.type}${c.nullable ? ' (aceita NULL)' : ' NOT NULL'}`}>
                                          <span className="truncate">{c.name}</span>
                                          <span className="ml-auto shrink-0 text-muted">{c.type}{c.nullable ? '' : ' · not null'}</span>
                                        </li>
                                      ))
                                    )}
                                    {index.columnsLoaded && index.columnsOf(o).length === 0 && <li className="px-1 text-xs text-muted">Sem colunas visíveis.</li>}
                                  </ul>
                                )}
                              </li>
                            )
                          })}
                          {items.length > limit && (
                            <li>
                              <button className="px-2 py-0.5 text-xs text-muted underline" onClick={() => setShown((s) => ({ ...s, [gKey]: limit + PAGE }))}>
                                Mostrar mais ({items.length - limit})
                              </button>
                            </li>
                          )}
                        </ul>
                      )}
                    </div>
                  )
                })}
              </div>
            )}
          </div>
        )
      })}
    </div>
  )
}
