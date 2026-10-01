import { describe, expect, it } from 'vitest'
import type { Cell, ColumnKind } from './contracts'
import { compareCells, nextSort, sortedOrder, type SortKey } from './gridSort'

describe('nextSort', () => {
  it('alterna crescente, decrescente e original ao clicar na mesma coluna', () => {
    let keys: SortKey[] = []
    keys = nextSort(keys, 1, false)
    expect(keys).toEqual([{ col: 1, dir: 'asc' }])
    keys = nextSort(keys, 1, false)
    expect(keys).toEqual([{ col: 1, dir: 'desc' }])
    keys = nextSort(keys, 1, false)
    expect(keys).toEqual([])
  })

  it('clicar em outra coluna sem Shift substitui as chaves', () => {
    expect(nextSort([{ col: 0, dir: 'desc' }], 2, false)).toEqual([{ col: 2, dir: 'asc' }])
  })

  it('clicar sem Shift numa coluna que está entre várias chaves reinicia só com ela', () => {
    expect(nextSort([{ col: 0, dir: 'asc' }, { col: 1, dir: 'asc' }], 1, false)).toEqual([{ col: 1, dir: 'asc' }])
  })

  it('Shift acrescenta colunas e alterna a existente sem tocar nas outras', () => {
    let keys = nextSort([{ col: 0, dir: 'asc' }], 3, true)
    expect(keys).toEqual([{ col: 0, dir: 'asc' }, { col: 3, dir: 'asc' }])
    keys = nextSort(keys, 0, true)
    expect(keys).toEqual([{ col: 0, dir: 'desc' }, { col: 3, dir: 'asc' }])
    keys = nextSort(keys, 0, true)
    expect(keys).toEqual([{ col: 3, dir: 'asc' }])
  })
})

describe('compareCells', () => {
  it('NULL vem antes de qualquer valor', () => {
    expect(compareCells(null, 'a', 'text')).toBeLessThan(0)
    expect(compareCells(5, null, 'number')).toBeGreaterThan(0)
    expect(compareCells(null, null, 'number')).toBe(0)
  })

  it('números comparam por valor, não por texto', () => {
    expect(compareCells(10, 9, 'number')).toBeGreaterThan(0)
    expect(compareCells('1234.50', '99.9', 'number')).toBeGreaterThan(0) // decimal chega como texto
  })

  it('texto usa collation pt-BR (acentos e caixa não jogam para o fim)', () => {
    const words = ['zebra', 'Árvore', 'abelha', 'Zulu']
    const sorted = [...words].sort((a, b) => compareCells(a, b, 'text'))
    expect(sorted).toEqual(['abelha', 'Árvore', 'zebra', 'Zulu'])
  })

  it('datas ISO ordenam cronologicamente', () => {
    expect(compareCells('2026-01-02 00:00:00', '2025-12-31 23:59:59', 'date')).toBeGreaterThan(0)
  })

  it('bit: false antes de true', () => {
    expect(compareCells(false, true, 'bool')).toBeLessThan(0)
  })
})

describe('sortedOrder', () => {
  const kinds: ColumnKind[] = ['text', 'number']
  const rows: Cell[][] = [
    ['b', 2],
    ['a', 10],
    ['b', 1],
    ['a', 3],
    [null, 7],
  ]

  it('sem chaves devolve a ordem original', () => {
    expect([...sortedOrder(rows, kinds, [])]).toEqual([0, 1, 2, 3, 4])
  })

  it('ordena por uma coluna, com estabilidade nos empates', () => {
    expect([...sortedOrder(rows, kinds, [{ col: 0, dir: 'asc' }])]).toEqual([4, 1, 3, 0, 2])
  })

  it('ordena por várias colunas', () => {
    const order = sortedOrder(rows, kinds, [{ col: 0, dir: 'asc' }, { col: 1, dir: 'desc' }])
    expect([...order]).toEqual([4, 1, 3, 0, 2])
    const order2 = sortedOrder(rows, kinds, [{ col: 0, dir: 'desc' }, { col: 1, dir: 'asc' }])
    expect([...order2]).toEqual([2, 0, 3, 1, 4])
  })

  it('descendente inverte NULL para o fim', () => {
    const order = sortedOrder(rows, kinds, [{ col: 1, dir: 'desc' }])
    expect([...order]).toEqual([1, 4, 3, 0, 2])
  })

  it('ordena 10 mil linhas rapidamente', () => {
    const big: Cell[][] = Array.from({ length: 10_000 }, (_, i) => [`n${(i * 7919) % 10_000}`, (i * 31) % 997])
    const t = performance.now()
    sortedOrder(big, kinds, [{ col: 0, dir: 'asc' }, { col: 1, dir: 'asc' }])
    expect(performance.now() - t).toBeLessThan(1000)
  })
})
