import { describe, expect, it } from 'vitest'
import { DEFAULT_SETTINGS, MAX_ROWS, MIN_ROWS, normalize } from './settings'

describe('normalize', () => {
  it('sem nada, devolve os padrões', () => {
    expect(normalize(undefined)).toEqual(DEFAULT_SETTINGS)
    expect(normalize(null)).toEqual(DEFAULT_SETTINGS)
    expect(normalize('lixo')).toEqual(DEFAULT_SETTINGS)
    expect(normalize({})).toEqual(DEFAULT_SETTINGS)
  })

  it('aceita valores válidos, inclusive digitados como texto', () => {
    expect(normalize({ maxRows: '5000', commandTimeout: '120', autoAlias: false, csvDelimiter: ',' })).toEqual({
      maxRows: 5000, commandTimeout: 120, autoAlias: false, csvDelimiter: ',', aiEnabled: false,
    })
  })

  it('ajusta o limite de linhas à faixa permitida', () => {
    expect(normalize({ maxRows: 5 }).maxRows).toBe(MIN_ROWS)
    expect(normalize({ maxRows: 99_000_000 }).maxRows).toBe(MAX_ROWS)
    expect(normalize({ maxRows: 1234.6 }).maxRows).toBe(1235)
  })

  it('valor não numérico volta ao padrão em vez de virar NaN', () => {
    expect(normalize({ maxRows: 'abc' }).maxRows).toBe(DEFAULT_SETTINGS.maxRows)
    expect(normalize({ maxRows: '' }).maxRows).toBe(DEFAULT_SETTINGS.maxRows)
    expect(normalize({ commandTimeout: NaN }).commandTimeout).toBe(DEFAULT_SETTINGS.commandTimeout)
  })

  it('timeout aceita 0 (sem limite) e não aceita negativo', () => {
    expect(normalize({ commandTimeout: 0 }).commandTimeout).toBe(0)
    expect(normalize({ commandTimeout: -5 }).commandTimeout).toBe(0)
  })

  it('separador desconhecido vira ponto e vírgula', () => {
    expect(normalize({ csvDelimiter: '|' }).csvDelimiter).toBe(';')
    expect(normalize({ csvDelimiter: ',' }).csvDelimiter).toBe(',')
  })

  it('alias automático só aceita booleano', () => {
    expect(normalize({ autoAlias: 'false' }).autoAlias).toBe(true)
    expect(normalize({ autoAlias: false }).autoAlias).toBe(false)
  })
})
