import type { Cell, GuardChange } from './contracts'

/** Para cada linha da amostra, quais colunas mudaram entre o antes e o depois do UPDATE. */
export function changedCells(before: readonly Cell[][], after: readonly Cell[][]): boolean[][] {
  return before.map((row, i) => row.map((v, c) => (after[i]?.[c] ?? null) !== v))
}

const plural = (n: number, one: string, many: string) => `${n.toLocaleString('pt-BR')} ${n === 1 ? one : many}`

/** Frase de destaque do diálogo de segunda confirmação para um statement perigoso. */
export function describeChange(c: GuardChange): string {
  const target = c.target ?? 'o objeto'
  const n = c.affectedRows
  switch (c.kind) {
    case 'updateWithoutWhere':
      return n === undefined ? `UPDATE em ${target}: quantidade de linhas não disponível` : `${plural(n, 'linha atualizada', 'linhas atualizadas')} em ${target}`
    case 'deleteWithoutWhere':
      return n === undefined ? `DELETE em ${target}: quantidade de linhas não disponível` : `${plural(n, 'linha apagada', 'linhas apagadas')} de ${target}`
    case 'truncateTable':
      return n === undefined ? `TRUNCATE em ${target}: quantidade de linhas não disponível` : `TRUNCATE de ${target}: ${plural(n, 'linha perdida', 'linhas perdidas')}`
    case 'drop':
      return n === undefined ? c.description : `${c.description} (${plural(n, 'linha perdida', 'linhas perdidas')})`
    case 'alterTable':
      return c.description || `ALTER TABLE em ${target}`
    default:
      return c.description
  }
}

/** A amostra mostra menos linhas do que foram afetadas? */
export function isSampleTruncated(c: GuardChange): boolean {
  return c.hasPreview && c.affectedRows !== undefined && c.before.length < c.affectedRows
}
