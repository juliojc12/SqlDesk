import { describe, expect, it } from 'vitest'
import { buildIndex, type MetadataDto } from './metadataIndex'

const base: MetadataDto = {
  loaded: true, columnsLoaded: true, loading: false, hasSchemaLevel: false,
  schemas: ['alfa', 'beta'],
  objects: [
    { schema: 'alfa', name: 'x', type: 'table' },
    { schema: 'beta', name: 'x', type: 'table' },
  ],
  columns: {},
}

describe('MetaIndex sem nível de schema (MySQL)', () => {
  it('sem schema, prefere o banco atual da conexão', () => {
    const index = buildIndex(base, 'beta')
    expect(index.hasSchemaLevel).toBe(false)
    expect(index.find(undefined, 'x')?.schema).toBe('beta')
  })

  it('o banco atual vale sem diferenciar maiúsculas e minúsculas', () => {
    expect(buildIndex(base, 'ALFA').find(undefined, 'x')?.schema).toBe('alfa')
  })

  it('não prefere dbo mesmo que exista um banco com esse nome', () => {
    const dto = { ...base, schemas: ['dbo', 'beta'], objects: [{ schema: 'dbo', name: 'x', type: 'table' as const }, { schema: 'beta', name: 'x', type: 'table' as const }] }
    expect(buildIndex(dto, 'beta').find(undefined, 'x')?.schema).toBe('beta')
  })

  it('sem banco atual conhecido, fica com o primeiro candidato', () => {
    expect(buildIndex(base).find(undefined, 'x')?.schema).toBe('alfa')
  })

  it('com schema explícito, ignora o banco atual', () => {
    expect(buildIndex(base, 'beta').find('alfa', 'x')?.schema).toBe('alfa')
  })

  it('o banco atual pode ser definido depois de montar o índice', () => {
    const index = buildIndex(base)
    index.defaultSchema = 'beta'
    expect(index.find(undefined, 'x')?.schema).toBe('beta')
  })
})

describe('MetaIndex com nível de schema (SQL Server)', () => {
  it('prefere dbo e trata hasSchemaLevel ausente como verdadeiro', () => {
    const { hasSchemaLevel: _omit, ...semCampo } = base
    const dto = { ...semCampo, schemas: ['dbo', 'beta'], objects: [{ schema: 'beta', name: 'x', type: 'table' as const }, { schema: 'dbo', name: 'x', type: 'table' as const }] }
    const index = buildIndex(dto, 'beta')
    expect(index.hasSchemaLevel).toBe(true)
    expect(index.find(undefined, 'x')?.schema).toBe('dbo')
  })
})
