import { describe, expect, it } from 'vitest'
import type { Cell, ColumnInfo } from './contracts'
import { buildLoadedPayload, formatDuration, identityView, resultOrdinal, suggestedFileName } from './exporter'
import type { ResultSet } from './results'

const cols: ColumnInfo[] = [
  { name: 'Id', kind: 'number', typeName: 'int' },
  { name: 'Nome', kind: 'text', typeName: 'nvarchar' },
  { name: 'Valor', kind: 'number', typeName: 'decimal' },
]
const rows: Cell[][] = [
  [1, 'Zeca', '10.5'],
  [2, 'Ana', '200.25'],
  [3, null, '3'],
]
const set = (over: Partial<ResultSet> = {}): ResultSet => ({
  key: 'e1:0', title: 'Resultado 1', columns: cols, rows, rowCount: rows.length, truncated: false, done: true,
  source: { start: 0, length: 10 }, sourceText: 'SELECT 1', ...over,
})

describe('buildLoadedPayload', () => {
  it('sem ordenação nem reordenação, exporta como chegou', () => {
    const p = buildLoadedPayload(set(), identityView(3))
    expect(p.columns.map((c) => c.name)).toEqual(['Id', 'Nome', 'Valor'])
    expect(p.rows).toEqual(rows)
  })

  it('respeita a ordem das colunas arrastadas', () => {
    const p = buildLoadedPayload(set(), { colOrder: [2, 0, 1], sort: [] })
    expect(p.columns.map((c) => c.name)).toEqual(['Valor', 'Id', 'Nome'])
    expect(p.rows[0]).toEqual(['10.5', 1, 'Zeca'])
  })

  it('respeita a ordenação atual da grade (por tipo, não por texto)', () => {
    const p = buildLoadedPayload(set(), { colOrder: [0, 1, 2], sort: [{ col: 2, dir: 'desc' }] })
    expect(p.rows.map((r) => r[0])).toEqual([2, 1, 3]) // 200.25, 10.5, 3 (numérico; como texto seria 3 > 200 > 10)
  })

  it('ordenação por texto com NULL primeiro', () => {
    const p = buildLoadedPayload(set(), { colOrder: [0, 1, 2], sort: [{ col: 1, dir: 'asc' }] })
    expect(p.rows.map((r) => r[1])).toEqual([null, 'Ana', 'Zeca'])
  })

  it('ordem de colunas inconsistente com o resultado cai na ordem original', () => {
    const p = buildLoadedPayload(set(), { colOrder: [0], sort: [] })
    expect(p.columns).toHaveLength(3)
  })

  it('só exporta o que está carregado (rowCount)', () => {
    const many = Array.from({ length: 5 }, (_, i) => [i, 'x', '1'] as Cell[])
    const p = buildLoadedPayload(set({ rows: many, rowCount: 3 }), identityView(3))
    expect(p.rows).toHaveLength(3)
  })
})

describe('resultOrdinal', () => {
  const a = set({ key: 'e1:0', source: { start: 0, length: 20 } })
  const b = set({ key: 'e1:1', source: { start: 0, length: 20 } })
  const c = set({ key: 'e1:2', source: { start: 30, length: 12 } })
  const other = set({ key: 'e0:0', source: { start: 0, length: 20 } })

  it('posição entre os resultados do mesmo trecho e da mesma execução', () => {
    const all = [other, a, b, c]
    expect(resultOrdinal(all, a)).toBe(0)
    expect(resultOrdinal(all, b)).toBe(1)
    expect(resultOrdinal(all, c)).toBe(0) // outro trecho: reinicia
    expect(resultOrdinal(all, other)).toBe(0)
  })
})

describe('suggestedFileName', () => {
  it('tira .sql e caracteres inválidos', () => {
    expect(suggestedFileName('Relatório.sql', 'Resultado 1', 'csv')).toBe('Relatório - Resultado 1.csv')
    expect(suggestedFileName('a/b:c', 'R*1', 'xlsx')).toBe('a_b_c - R_1.xlsx')
    expect(suggestedFileName('  ', '', 'csv')).toBe('consulta - resultado.csv')
  })
})

describe('formatDuration', () => {
  it('ms, segundos e minutos', () => {
    expect(formatDuration(340)).toBe('340 ms')
    expect(formatDuration(2500)).toBe('2,5 s')
    expect(formatDuration(125_000)).toBe('2 min 5 s')
  })
})
