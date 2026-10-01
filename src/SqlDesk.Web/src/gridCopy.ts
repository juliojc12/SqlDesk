import type { Cell } from './contracts'
import { isSelected, rowBounds, type Selection } from './gridSelection'

/** Um valor para colar no Excel: NULL vira célula vazia; texto com tab, quebra de linha ou aspas vai entre aspas. */
export function tsvCell(value: Cell): string {
  if (value === null) return ''
  const s = typeof value === 'boolean' ? (value ? 'true' : 'false') : String(value)
  return /[\t\r\n"]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s
}

/**
 * Texto tabulado de um retângulo de células. `rowIndexes` já vem na ordem exibida (ordenação aplicada) e
 * `colIndexes` na ordem das colunas exibidas (reordenação aplicada).
 */
export function toTsv(
  rows: readonly Cell[][],
  names: readonly string[],
  rowIndexes: readonly number[],
  colIndexes: readonly number[],
  withHeader: boolean,
): string {
  const lines: string[] = []
  if (withHeader) lines.push(colIndexes.map((c) => tsvCell(names[c])).join('\t'))
  for (const r of rowIndexes) lines.push(colIndexes.map((c) => tsvCell(rows[r][c])).join('\t'))
  return lines.join('\r\n')
}

/**
 * Texto tabulado de uma seleção qualquer (blocos, linhas, colunas e células soltas). Só entram as linhas e as colunas que têm
 * alguma célula selecionada; as células não selecionadas que ficam no meio saem vazias, para o que se cola no Excel manter
 * o alinhamento. `sourceRow` converte a linha de exibição na linha original (ordenação) e `colOrder` a coluna de exibição na original.
 */
export function toTsvSelection(
  rows: readonly Cell[][],
  names: readonly string[],
  sourceRow: (displayRow: number) => number,
  colOrder: readonly number[],
  sel: Selection,
  total: number,
  withHeader: boolean,
): string {
  const bounds = rowBounds(sel)
  if (!bounds) return ''
  const candidateCols = new Set<number>()
  for (const x of sel.rects) for (let c = x.c0; c <= Math.min(x.c1, colOrder.length - 1); c++) candidateCols.add(c)

  const selectedRows: number[] = []
  const usedCols = new Set<number>()
  for (let r = bounds.r0; r <= Math.min(bounds.r1, total - 1); r++) {
    let any = false
    for (const c of candidateCols) {
      if (isSelected(sel, r, c)) {
        any = true
        usedCols.add(c)
      }
    }
    if (any) selectedRows.push(r)
  }
  const cols = [...usedCols].sort((a, b) => a - b)

  const lines: string[] = []
  if (withHeader) lines.push(cols.map((c) => tsvCell(names[colOrder[c]])).join('\t'))
  for (const r of selectedRows) {
    const row = rows[sourceRow(r)]
    lines.push(cols.map((c) => (isSelected(sel, r, c) ? tsvCell(row[colOrder[c]]) : '')).join('\t'))
  }
  return lines.join('\r\n')
}
