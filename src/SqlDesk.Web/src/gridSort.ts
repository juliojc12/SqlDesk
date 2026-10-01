import type { Cell, ColumnKind } from './contracts'

export type SortDir = 'asc' | 'desc'
export interface SortKey {
  col: number
  dir: SortDir
}

/**
 * Clique no cabeçalho: alterna crescente, decrescente e original. Sem Shift, a coluna clicada passa a ser a única
 * chave; com Shift, entra (ou alterna) junto das demais.
 */
export function nextSort(current: readonly SortKey[], col: number, additive: boolean): SortKey[] {
  const existing = current.find((k) => k.col === col)
  const cycle = (k: SortKey): SortKey | null => (k.dir === 'asc' ? { col, dir: 'desc' } : null)

  if (additive) {
    if (!existing) return [...current, { col, dir: 'asc' }]
    const next = cycle(existing)
    return current.flatMap((k) => (k.col !== col ? [k] : next ? [next] : []))
  }
  if (!existing || current.length > 1) return [{ col, dir: 'asc' }]
  const next = cycle(existing)
  return next ? [next] : []
}

const collator = new Intl.Collator('pt-BR')

/** Menor primeiro; NULL antes de qualquer valor. Compara pelo tipo da coluna, não pelo texto exibido. */
export function compareCells(a: Cell, b: Cell, kind: ColumnKind): number {
  if (a === null || b === null) return a === b ? 0 : a === null ? -1 : 1
  switch (kind) {
    case 'number': {
      const [x, y] = [Number(a), Number(b)]
      return x < y ? -1 : x > y ? 1 : 0
    }
    case 'bool':
      return Number(a) - Number(b)
    case 'date':
    case 'binary': {
      // Datas chegam em ISO (yyyy-MM-dd HH:mm:ss...), que ordena como texto.
      const [x, y] = [String(a), String(b)]
      return x < y ? -1 : x > y ? 1 : 0
    }
    default:
      return collator.compare(String(a), String(b))
  }
}

/**
 * Índices das linhas na ordem exibida. Sem chaves, é a ordem original. Empates mantêm a ordem original
 * (o `sort` do JS é estável), então "ordenar e voltar" é determinístico.
 */
export function sortedOrder(rows: readonly Cell[][], kinds: readonly ColumnKind[], keys: readonly SortKey[], count = rows.length): Uint32Array {
  const order = new Uint32Array(count)
  for (let i = 0; i < order.length; i++) order[i] = i
  if (keys.length === 0) return order

  order.sort((i, j) => {
    for (const k of keys) {
      const c = compareCells(rows[i][k.col], rows[j][k.col], kinds[k.col])
      if (c !== 0) return k.dir === 'asc' ? c : -c
    }
    return 0
  })
  return order
}
