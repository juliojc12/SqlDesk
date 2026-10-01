import type { Cell, ColumnInfo } from './contracts'
import { sortedOrder, type SortKey } from './gridSort'
import type { ResultSet } from './results'

/** Estado visível da grade: ordem das colunas (índices originais) e ordenação por clique no cabeçalho. */
export interface GridView {
  colOrder: number[]
  sort: SortKey[]
}

export const identityView = (columnCount: number): GridView => ({ colOrder: Array.from({ length: columnCount }, (_, i) => i), sort: [] })

/**
 * Linhas e colunas como a grade as mostra agora: a exportação respeita a ordem das colunas e a ordenação atuais.
 * Só entram as linhas carregadas.
 */
export function buildLoadedPayload(set: ResultSet, view: GridView): { columns: ColumnInfo[]; rows: Cell[][] } {
  const order = sortedOrder(set.rows, set.columns.map((c) => c.kind), view.sort, set.rowCount)
  const colOrder = view.colOrder.length === set.columns.length ? view.colOrder : identityView(set.columns.length).colOrder
  const rows = new Array<Cell[]>(order.length)
  for (let i = 0; i < order.length; i++) {
    const src = set.rows[order[i]]
    rows[i] = colOrder.map((c) => src[c])
  }
  return { columns: colOrder.map((c) => set.columns[c]), rows }
}

/**
 * Posição do result set entre os que o mesmo trecho produz (um texto com vários SELECT gera vários resultados): é o que
 * o backend usa para escolher qual gravar ao reexecutar.
 */
export function resultOrdinal(sets: readonly ResultSet[], set: ResultSet): number {
  const run = set.key.split(':')[0]
  return sets.filter((s) => s.key.startsWith(`${run}:`) && s.source.start === set.source.start && s.source.length === set.source.length).indexOf(set)
}

/** Nome de arquivo sugerido: título da aba (sem .sql e sem caracteres inválidos no Windows) e o resultado. */
export function suggestedFileName(tabTitle: string, resultTitle: string, ext: 'csv' | 'xlsx'): string {
  const clean = (s: string) => [...s.replace(/\.sql$/i, '')].filter((ch) => ch.charCodeAt(0) >= 32).join('').replace(/[<>:"/\\|?*]/g, '_').trim()
  const base = `${clean(tabTitle) || 'consulta'} - ${clean(resultTitle) || 'resultado'}`
  return `${base}.${ext}`
}

export function formatDuration(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)} ms`
  if (ms < 60_000) return `${(ms / 1000).toFixed(1).replace('.', ',')} s`
  return `${Math.floor(ms / 60_000)} min ${Math.floor((ms % 60_000) / 1000)} s`
}
