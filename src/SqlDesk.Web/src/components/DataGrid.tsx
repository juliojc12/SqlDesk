import { useEffect, useMemo, useRef, useState } from 'react'
import type { Cell, ColumnInfo } from '../contracts'
import type { GridView } from '../exporter'
import { toTsv } from '../gridCopy'
import { nextSort, sortedOrder, type SortKey } from '../gridSort'
import type { ResultSet } from '../results'

const ROW_H = 30
const HEADER_H = 34
const ROWNUM_W = 56
const OVERSCAN = 6
const MIN_COL_W = 48

interface Pos { r: number; c: number }
interface Sel { anchor: Pos; focus: Pos }

interface Props {
  set: ResultSet
  color: string
  copyWithHeader: boolean
  onCopyWithHeaderChange: (v: boolean) => void
  onLoadAll: () => void
  canLoadAll: boolean
  /** Informa a ordem de colunas e a ordenação atuais (a exportação os respeita). */
  onViewChange?: (view: GridView) => void
}

function initialWidth(col: ColumnInfo, sample: readonly Cell[][], index: number): number {
  let chars = col.name.length + 3
  for (let i = 0; i < Math.min(sample.length, 100); i++) {
    const v = sample[i][index]
    if (v !== null) chars = Math.max(chars, String(v).length)
  }
  return Math.min(360, Math.max(80, Math.round(chars * 8.5 + 28)))
}

async function copyText(text: string) {
  try {
    await navigator.clipboard.writeText(text)
  } catch {
    // Sem permissão da API assíncrona: cai para o comando antigo, que funciona dentro de um gesto do usuário.
    const ta = document.createElement('textarea')
    ta.value = text
    ta.style.position = 'fixed'
    ta.style.opacity = '0'
    document.body.appendChild(ta)
    ta.select()
    document.execCommand('copy')
    ta.remove()
  }
}

function Value({ value, kind }: { value: Cell; kind: ColumnInfo['kind'] }) {
  if (value === null) return <span className="rounded bg-hover px-1.5 text-[12px] italic text-muted">NULL</span>
  if (kind === 'bool') return <>{value ? 'true' : 'false'}</>
  return <>{String(value)}</>
}

/**
 * Grade virtualizada: só as linhas visíveis existem no DOM. Ordenação, redimensionamento, reordenação de colunas
 * e seleção/cópia vivem aqui, no cliente, sem reexecutar a query.
 */
export function DataGrid({ set, color, copyWithHeader, onCopyWithHeaderChange, onLoadAll, canLoadAll, onViewChange }: Props) {
  const { columns, rows } = set
  const scroller = useRef<HTMLDivElement>(null)
  const [scrollTop, setScrollTop] = useState(0)
  const [viewH, setViewH] = useState(300)
  const [sort, setSort] = useState<SortKey[]>([])
  const [colOrder, setColOrder] = useState<number[]>(() => columns.map((_, i) => i))
  const [widths, setWidths] = useState<number[]>(() => columns.map((c, i) => initialWidth(c, rows, i)))
  const [sel, setSel] = useState<Sel | null>(null)
  const autosized = useRef(rows.length > 0)
  const dragging = useRef(false)
  const dragFrom = useRef<number | null>(null)

  // Ajusta a largura inicial das colunas quando chegam as primeiras linhas.
  useEffect(() => {
    if (autosized.current || rows.length === 0) return
    autosized.current = true
    setWidths(columns.map((c, i) => initialWidth(c, rows, i)))
  }, [rows, rows.length, columns])

  useEffect(() => {
    const el = scroller.current
    if (!el) return
    const ro = new ResizeObserver(() => setViewH(el.clientHeight))
    ro.observe(el)
    setViewH(el.clientHeight)
    return () => ro.disconnect()
  }, [])

  useEffect(() => {
    const up = () => (dragging.current = false)
    window.addEventListener('mouseup', up)
    return () => window.removeEventListener('mouseup', up)
  }, [])

  useEffect(() => {
    onViewChange?.({ colOrder, sort })
  }, [colOrder, sort, onViewChange])

  const kinds = useMemo(() => columns.map((c) => c.kind), [columns])
  const total = Math.min(set.rowCount, rows.length)
  // `rows` é mutado no lugar: o tamanho (total) é o que diz quando recalcular.
  const order = useMemo(() => sortedOrder(rows, kinds, sort, total), [rows, total, kinds, sort])
  const totalW = ROWNUM_W + colOrder.reduce((sum, c) => sum + widths[c], 0)

  const first = Math.max(0, Math.floor(scrollTop / ROW_H) - OVERSCAN)
  const last = Math.min(total - 1, Math.ceil((scrollTop + viewH) / ROW_H) + OVERSCAN)

  const rect = sel && {
    r0: Math.min(sel.anchor.r, sel.focus.r), r1: Math.max(sel.anchor.r, sel.focus.r),
    c0: Math.min(sel.anchor.c, sel.focus.c), c1: Math.max(sel.anchor.c, sel.focus.c),
  }

  function copy(withHeader: boolean) {
    if (!rect) return
    const rowIdx = Array.from({ length: rect.r1 - rect.r0 + 1 }, (_, k) => order[rect.r0 + k])
    const colIdx = colOrder.slice(rect.c0, rect.c1 + 1)
    void copyText(toTsv(rows, columns.map((c) => c.name), rowIdx, colIdx, withHeader))
  }

  const scrollIntoView = (r: number) => {
    const el = scroller.current
    if (!el) return
    const top = r * ROW_H
    if (top < el.scrollTop) el.scrollTop = top
    else if (top + ROW_H > el.scrollTop + el.clientHeight - HEADER_H) el.scrollTop = top + ROW_H - el.clientHeight + HEADER_H
  }

  function onKeyDown(e: React.KeyboardEvent) {
    const key = e.key.toLowerCase()
    if (e.ctrlKey && key === 'c') {
      e.preventDefault()
      copy(e.shiftKey ? true : copyWithHeader)
    } else if (e.ctrlKey && key === 'a') {
      e.preventDefault()
      if (total > 0) setSel({ anchor: { r: 0, c: 0 }, focus: { r: total - 1, c: columns.length - 1 } })
    } else if (key.startsWith('arrow') && sel) {
      e.preventDefault()
      const dr = key === 'arrowdown' ? 1 : key === 'arrowup' ? -1 : 0
      const dc = key === 'arrowright' ? 1 : key === 'arrowleft' ? -1 : 0
      const focus = { r: Math.min(total - 1, Math.max(0, sel.focus.r + dr)), c: Math.min(columns.length - 1, Math.max(0, sel.focus.c + dc)) }
      setSel({ anchor: e.shiftKey ? sel.anchor : focus, focus })
      scrollIntoView(focus.r)
    }
  }

  function onCellDown(e: React.MouseEvent, r: number, c: number) {
    if (e.button !== 0) return
    dragging.current = true
    setSel((cur) => (e.shiftKey && cur ? { anchor: cur.anchor, focus: { r, c } } : { anchor: { r, c }, focus: { r, c } }))
  }

  function onRowNumberDown(e: React.MouseEvent, r: number) {
    if (e.button !== 0) return
    const lastCol = columns.length - 1
    setSel((cur) => (e.shiftKey && cur ? { anchor: { r: cur.anchor.r, c: 0 }, focus: { r, c: lastCol } } : { anchor: { r, c: 0 }, focus: { r, c: lastCol } }))
  }

  function startResize(e: React.MouseEvent, col: number) {
    e.preventDefault()
    e.stopPropagation()
    const startX = e.clientX
    const startW = widths[col]
    const move = (ev: MouseEvent) => setWidths((w) => w.map((x, i) => (i === col ? Math.max(MIN_COL_W, startW + ev.clientX - startX) : x)))
    const up = () => {
      window.removeEventListener('mousemove', move)
      window.removeEventListener('mouseup', up)
    }
    window.addEventListener('mousemove', move)
    window.addEventListener('mouseup', up)
  }

  function moveColumn(from: number, to: number) {
    if (from === to) return
    setColOrder((o) => {
      const next = o.slice()
      const [c] = next.splice(from, 1)
      next.splice(to, 0, c)
      return next
    })
    setSel(null)
  }

  const selBg = `color-mix(in srgb, ${color} 28%, transparent)`
  const visible: number[] = []
  for (let i = first; i <= last; i++) visible.push(i)

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div
        ref={scroller}
        tabIndex={0}
        role="grid"
        aria-label={set.title}
        aria-rowcount={total}
        aria-colcount={columns.length}
        className="thin-scroll relative min-h-0 flex-1 select-none overflow-auto outline-none"
        onScroll={(e) => setScrollTop(e.currentTarget.scrollTop)}
        onKeyDown={onKeyDown}
      >
        <div style={{ width: totalW, height: HEADER_H + total * ROW_H }} className="relative">
          <div role="row" style={{ height: HEADER_H, width: totalW }} className="sticky top-0 z-10 flex border-b border-line bg-surface text-[13px] font-semibold">
            <div style={{ width: ROWNUM_W }} className="shrink-0 border-r border-line" />
            {colOrder.map((c, p) => {
              const key = sort.findIndex((k) => k.col === c)
              return (
                <div
                  key={c}
                  role="columnheader"
                  aria-sort={key < 0 ? 'none' : sort[key].dir === 'asc' ? 'ascending' : 'descending'}
                  draggable
                  title={`${columns[c].name} (${columns[c].typeName})`}
                  style={{ width: widths[c] }}
                  className="relative flex shrink-0 cursor-pointer items-center gap-1 border-r border-line px-2 hover:bg-hover"
                  onClick={(e) => setSort((s) => nextSort(s, c, e.shiftKey))}
                  onDragStart={(e) => {
                    dragFrom.current = p
                    e.dataTransfer.effectAllowed = 'move'
                  }}
                  onDragOver={(e) => e.preventDefault()}
                  onDrop={(e) => {
                    e.preventDefault()
                    if (dragFrom.current !== null) moveColumn(dragFrom.current, p)
                    dragFrom.current = null
                  }}
                >
                  <span className="min-w-0 flex-1 truncate">{columns[c].name}</span>
                  {key >= 0 && (
                    <span className="flex shrink-0 items-center gap-0.5 text-[11px] text-muted">
                      {sort.length > 1 && <span>{key + 1}</span>}
                      <span aria-hidden>{sort[key].dir === 'asc' ? '▲' : '▼'}</span>
                    </span>
                  )}
                  <span
                    role="separator"
                    aria-orientation="vertical"
                    className="absolute right-0 top-0 h-full w-1.5 cursor-col-resize hover:bg-line"
                    onMouseDown={(e) => startResize(e, c)}
                    onClick={(e) => e.stopPropagation()}
                    draggable={false}
                  />
                </div>
              )
            })}
          </div>

          {visible.map((r) => {
            const src = rows[order[r]]
            const rowSelected = rect && r >= rect.r0 && r <= rect.r1
            return (
              <div
                key={r}
                role="row"
                aria-rowindex={r + 1}
                style={{ position: 'absolute', top: HEADER_H + r * ROW_H, height: ROW_H, width: totalW }}
                className="flex border-b border-line/60 text-[14px]"
              >
                <div
                  style={{ width: ROWNUM_W }}
                  className="shrink-0 cursor-default border-r border-line pr-2 text-right text-[12px] leading-[30px] text-muted"
                  onMouseDown={(e) => onRowNumberDown(e, r)}
                >
                  {r + 1}
                </div>
                {colOrder.map((c, p) => {
                  const selected = rowSelected && p >= rect.c0 && p <= rect.c1
                  const kind = columns[c].kind
                  const text = src[c]
                  return (
                    <div
                      key={c}
                      role="gridcell"
                      style={{ width: widths[c], backgroundColor: selected ? selBg : undefined }}
                      title={typeof text === 'string' && text.length > 40 ? text.slice(0, 500) : undefined}
                      className={`shrink-0 truncate border-r border-line/60 px-2 leading-[30px] ${kind === 'number' ? 'text-right tabular-nums' : ''}`}
                      onMouseDown={(e) => onCellDown(e, r, p)}
                      onMouseEnter={() => {
                        if (dragging.current) setSel((cur) => (cur ? { anchor: cur.anchor, focus: { r, c: p } } : cur))
                      }}
                    >
                      <Value value={text} kind={kind} />
                    </div>
                  )
                })}
              </div>
            )
          })}
        </div>
      </div>

      <div className="flex h-9 shrink-0 items-center gap-4 border-t border-line px-4 text-[13px] text-muted">
        <span>
          {total.toLocaleString('pt-BR')} {total === 1 ? 'linha' : 'linhas'}
          {!set.done && ' · carregando…'}
        </span>
        {set.truncated && (
          <span className="flex items-center gap-2 text-fg">
            Mostrando só as primeiras linhas.
            {canLoadAll && (
              <button className="rounded-md border border-line px-2 py-0.5 hover:bg-hover" onClick={onLoadAll}>
                Carregar todas
              </button>
            )}
          </span>
        )}
        <label className="ml-auto flex cursor-pointer items-center gap-2">
          <input type="checkbox" checked={copyWithHeader} onChange={(e) => onCopyWithHeaderChange(e.target.checked)} />
          Copiar com cabeçalho
        </label>
      </div>
    </div>
  )
}
