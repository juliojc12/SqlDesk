import type { Cell } from './contracts'

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
