import { describe, expect, it } from 'vitest'
import { toTsv, tsvCell } from './gridCopy'

describe('tsvCell', () => {
  it('NULL vira vazio e bit vira true/false', () => {
    expect(tsvCell(null)).toBe('')
    expect(tsvCell(true)).toBe('true')
    expect(tsvCell(42)).toBe('42')
  })

  it('protege tab, quebra de linha e aspas', () => {
    expect(tsvCell('a\tb')).toBe('"a\tb"')
    expect(tsvCell('linha1\nlinha2')).toBe('"linha1\nlinha2"')
    expect(tsvCell('diz "oi"')).toBe('"diz ""oi"""')
  })
})

describe('toTsv', () => {
  const rows = [
    [1, 'ana', null],
    [2, 'bia', 'x'],
    [3, 'cid', 'y'],
  ]
  const names = ['Id', 'Nome', 'Obs']

  it('copia as células na ordem de linhas e colunas informada', () => {
    expect(toTsv(rows, names, [2, 0], [1, 0], false)).toBe('cid\t3\r\nana\t1')
  })

  it('inclui o cabeçalho quando pedido', () => {
    expect(toTsv(rows, names, [1], [0, 2], true)).toBe('Id\tObs\r\n2\tx')
  })
})
