import { describe, expect, it } from 'vitest'
import type { GuardChange } from './contracts'
import { changedCells, describeChange, isSampleTruncated } from './guardDiff'

const base: GuardChange = {
  kind: 'updateWithoutWhere', target: 'dbo.Clientes', line: 1, description: 'x', affectedRows: 2,
  hasPreview: true, columns: ['Id', 'Nome'], before: [], after: [],
}

describe('changedCells', () => {
  it('marca só as colunas que mudaram', () => {
    expect(changedCells([[1, 'a'], [2, 'b']], [[1, 'X'], [2, 'b']])).toEqual([[false, true], [false, false]])
  })

  it('NULL para valor (e valor para NULL) conta como mudança', () => {
    expect(changedCells([[null, 'a']], [[5, null]])).toEqual([[true, true]])
    expect(changedCells([[null]], [[null]])).toEqual([[false]])
  })
})

describe('describeChange', () => {
  it('UPDATE e DELETE com singular e plural', () => {
    expect(describeChange({ ...base, affectedRows: 1 })).toBe('1 linha atualizada em dbo.Clientes')
    expect(describeChange({ ...base, affectedRows: 1500 })).toBe('1.500 linhas atualizadas em dbo.Clientes')
    expect(describeChange({ ...base, kind: 'deleteWithoutWhere', affectedRows: 3 })).toBe('3 linhas apagadas de dbo.Clientes')
  })

  it('TRUNCATE e DROP mostram as linhas perdidas', () => {
    expect(describeChange({ ...base, kind: 'truncateTable', affectedRows: 42 })).toBe('TRUNCATE de dbo.Clientes: 42 linhas perdidas')
    expect(describeChange({ ...base, kind: 'drop', affectedRows: 1, description: 'DROP TABLE dbo.A remove a tabela do banco de dados' }))
      .toBe('DROP TABLE dbo.A remove a tabela do banco de dados (1 linha perdida)')
  })

  it('sem contagem, não inventa número', () => {
    expect(describeChange({ ...base, affectedRows: undefined })).toContain('não disponível')
    expect(describeChange({ ...base, kind: 'drop', affectedRows: undefined, description: 'DROP VIEW v' })).toBe('DROP VIEW v')
  })
})

describe('isSampleTruncated', () => {
  it('amostra menor que o total de linhas afetadas', () => {
    expect(isSampleTruncated({ ...base, affectedRows: 500, before: new Array(200).fill([1, 'a']) })).toBe(true)
    expect(isSampleTruncated({ ...base, affectedRows: 2, before: [[1, 'a'], [2, 'b']] })).toBe(false)
    expect(isSampleTruncated({ ...base, hasPreview: false, affectedRows: 500 })).toBe(false)
  })
})
