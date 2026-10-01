import { describe, expect, it } from 'vitest'
import { toTsv, toTsvSelection, tsvCell } from './gridCopy'
import { emptySelection, pressCell, pressColumn } from './gridSelection'

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

describe('toTsvSelection', () => {
  const data = [
    [1, 'ana', 10],
    [2, 'bia', 20],
    [3, 'cid', 30],
    [4, 'dan', 40],
  ]
  const names = ['Id', 'Nome', 'Valor']
  const id = (r: number) => r
  const cols = [0, 1, 2]

  it('copia só as células soltas, ignorando o que está entre elas', () => {
    let s = pressCell(emptySelection, { r: 0, c: 1 }, { ctrl: false, shift: false }).sel
    s = pressCell(s, { r: 3, c: 1 }, { ctrl: true, shift: false }).sel
    expect(toTsvSelection(data, names, id, cols, s, 4, false)).toBe('ana\r\ndan')
  })

  it('células soltas em colunas diferentes mantêm o alinhamento com campos vazios', () => {
    let s = pressCell(emptySelection, { r: 0, c: 0 }, { ctrl: false, shift: false }).sel
    s = pressCell(s, { r: 2, c: 2 }, { ctrl: true, shift: false }).sel
    expect(toTsvSelection(data, names, id, cols, s, 4, false)).toBe('1\t\r\n\t30')
  })

  it('um bloco com uma célula tirada do meio deixa um vazio no lugar', () => {
    let s = pressCell(emptySelection, { r: 0, c: 0 }, { ctrl: false, shift: false }).sel
    s = pressCell(s, { r: 1, c: 1 }, { ctrl: false, shift: true }).sel
    s = pressCell(s, { r: 0, c: 1 }, { ctrl: true, shift: false }).sel
    expect(toTsvSelection(data, names, id, cols, s, 4, false)).toBe('1\t\r\n2\tbia')
  })

  it('cabeçalho só das colunas que têm célula selecionada, na ordem exibida', () => {
    const s = pressColumn(pressColumn(emptySelection, 2, 4, { ctrl: false, shift: false }), 0, 4, { ctrl: true, shift: false })
    expect(toTsvSelection(data, names, id, cols, s, 4, true).split('\r\n')[0]).toBe('Id\tValor')
  })

  it('respeita a ordenação e a reordenação das colunas', () => {
    const order = [3, 2, 1, 0] // exibição: linhas de baixo para cima
    const display = [1, 2, 0] // exibição: Nome, Valor, Id
    const s = pressColumn(emptySelection, 0, 4, { ctrl: false, shift: false }) // 1ª coluna exibida = Nome
    expect(toTsvSelection(data, names, (r) => order[r], display, s, 4, false)).toBe('dan\r\ncid\r\nbia\r\nana')
  })

  it('seleção vazia não copia nada', () => {
    expect(toTsvSelection(data, names, id, cols, emptySelection, 4, true)).toBe('')
  })
})
