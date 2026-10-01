import { describe, expect, it } from 'vitest'
import {
  dragTo, emptySelection, isColumnSelected, isSelected, moveFocus, pressCell, pressColumn, pressRow, selectAll, selectedCount,
  type Mods, type Selection,
} from './gridSelection'

const none: Mods = { ctrl: false, shift: false }
const ctrl: Mods = { ctrl: true, shift: false }
const shift: Mods = { ctrl: false, shift: true }
const press = (sel: Selection, r: number, c: number, m: Mods = none) => pressCell(sel, { r, c }, m).sel
const cells = (sel: Selection, rows: number, cols: number) => {
  const out: string[] = []
  for (let r = 0; r < rows; r++) for (let c = 0; c < cols; c++) if (isSelected(sel, r, c)) out.push(`${r},${c}`)
  return out
}

describe('clique simples', () => {
  it('seleciona só a célula clicada e troca a seleção anterior', () => {
    let s = press(emptySelection, 2, 1)
    expect(cells(s, 5, 5)).toEqual(['2,1'])
    s = press(s, 4, 3)
    expect(cells(s, 5, 5)).toEqual(['4,3'])
  })
})

describe('Ctrl+clique: células soltas', () => {
  it('soma células sem selecionar o que está entre elas', () => {
    let s = press(emptySelection, 0, 0)
    s = press(s, 4, 0, ctrl)
    s = press(s, 2, 2, ctrl)
    expect(cells(s, 5, 3)).toEqual(['0,0', '2,2', '4,0'])
  })

  it('Ctrl+clique numa célula já selecionada tira só ela', () => {
    let s = press(emptySelection, 0, 0)
    s = pressCell(s, { r: 3, c: 0 }, shift).sel // bloco 0..3 na coluna 0
    expect(cells(s, 5, 1)).toEqual(['0,0', '1,0', '2,0', '3,0'])
    s = press(s, 1, 0, ctrl)
    expect(cells(s, 5, 1)).toEqual(['0,0', '2,0', '3,0'])
    expect(selectedCount(s)).toBe(3)
  })

  it('marcar de novo uma célula tirada a devolve à seleção', () => {
    let s = pressCell(press(emptySelection, 0, 0), { r: 3, c: 0 }, shift).sel
    s = press(s, 1, 0, ctrl) // tira
    s = press(s, 1, 0, ctrl) // volta
    expect(cells(s, 5, 1)).toEqual(['0,0', '1,0', '2,0', '3,0'])
  })

  it('tirar uma célula não deixa o arrasto seguinte estender o bloco', () => {
    const s0 = pressCell(press(emptySelection, 0, 0), { r: 3, c: 0 }, shift).sel
    expect(pressCell(s0, { r: 1, c: 0 }, ctrl).dragging).toBe(false)
    expect(pressCell(s0, { r: 4, c: 0 }, ctrl).dragging).toBe(true)
  })
})

describe('Shift e arrastar: blocos', () => {
  it('Shift+clique estende o bloco a partir do ponto de partida', () => {
    let s = press(emptySelection, 1, 1)
    s = press(s, 3, 2, shift)
    expect(cells(s, 5, 4)).toEqual(['1,1', '1,2', '2,1', '2,2', '3,1', '3,2'])
    s = press(s, 2, 1, shift) // encolhe de novo, sempre a partir de (1,1)
    expect(cells(s, 5, 4)).toEqual(['1,1', '2,1'])
  })

  it('arrastar com Ctrl cria um segundo bloco sem perder o primeiro', () => {
    let s = press(emptySelection, 0, 0)
    s = dragTo(s, { r: 1, c: 0 }) // bloco 1: linhas 0..1 da coluna 0
    const pressed = pressCell(s, { r: 4, c: 2 }, ctrl)
    s = dragTo(pressed.sel, { r: 5, c: 3 })
    expect(cells(s, 7, 4)).toEqual(['0,0', '1,0', '4,2', '4,3', '5,2', '5,3'])
  })

  it('o bloco novo vale mais do que células que o Ctrl+clique tinha tirado antes', () => {
    let s = pressCell(press(emptySelection, 0, 0), { r: 3, c: 0 }, shift).sel
    s = press(s, 1, 0, ctrl) // tira (1,0)
    s = pressCell(s, { r: 0, c: 0 }, ctrl).sel // (0,0) está selecionada: tira também
    s = press(s, 1, 0, ctrl) // (1,0) fora: marca de novo
    expect(isSelected(s, 1, 0)).toBe(true)
    expect(isSelected(s, 0, 0)).toBe(false)
  })
})

describe('linhas e colunas', () => {
  it('clique no título seleciona a coluna; Ctrl soma outra coluna sem a do meio', () => {
    let s = pressColumn(emptySelection, 0, 4, none)
    s = pressColumn(s, 2, 4, ctrl)
    expect(cells(s, 4, 3)).toEqual(['0,0', '0,2', '1,0', '1,2', '2,0', '2,2', '3,0', '3,2'])
    expect(isColumnSelected(s, 0, 4)).toBe(true)
    expect(isColumnSelected(s, 1, 4)).toBe(false)
  })

  it('Shift+clique no título estende de coluna em coluna', () => {
    let s = pressColumn(emptySelection, 1, 3, none)
    s = pressColumn(s, 3, 3, shift)
    expect(isColumnSelected(s, 1, 3) && isColumnSelected(s, 2, 3) && isColumnSelected(s, 3, 3)).toBe(true)
    expect(isColumnSelected(s, 0, 3)).toBe(false)
  })

  it('Ctrl+clique numa coluna já selecionada a tira', () => {
    let s = pressColumn(emptySelection, 0, 3, none)
    s = pressColumn(s, 2, 3, ctrl)
    s = pressColumn(s, 0, 3, ctrl)
    expect(isColumnSelected(s, 0, 3)).toBe(false)
    expect(isColumnSelected(s, 2, 3)).toBe(true)
  })

  it('uma célula tirada de dentro da coluna a deixa de ser "coluna selecionada"', () => {
    let s = pressColumn(emptySelection, 1, 4, none)
    s = press(s, 2, 1, ctrl)
    expect(isColumnSelected(s, 1, 4)).toBe(false)
    expect(isSelected(s, 2, 1)).toBe(false)
    expect(isSelected(s, 3, 1)).toBe(true)
  })

  it('número da linha seleciona a linha toda; Ctrl soma outra linha', () => {
    let s = pressRow(emptySelection, 1, 2, none)
    s = pressRow(s, 3, 2, ctrl)
    expect(cells(s, 5, 3)).toEqual(['1,0', '1,1', '1,2', '3,0', '3,1', '3,2'])
  })

  it('Shift+clique no número da linha estende o bloco de linhas', () => {
    let s = pressRow(emptySelection, 1, 1, none)
    s = pressRow(s, 3, 1, shift)
    expect(cells(s, 5, 2)).toHaveLength(6)
  })

  it('selecionar a coluna de 100 mil linhas custa um retângulo, não 100 mil células', () => {
    const s = pressColumn(emptySelection, 0, 100_000, none)
    expect(s.rects).toHaveLength(1)
    expect(isSelected(s, 99_999, 0)).toBe(true)
  })
})

describe('Ctrl+A e teclado', () => {
  it('selectAll cobre tudo', () => {
    const s = selectAll(3, 2)
    expect(cells(s, 3, 2)).toHaveLength(6)
    expect(selectAll(0, 2)).toBe(emptySelection)
  })

  it('setas movem a célula ativa e Shift+setas estendem o bloco', () => {
    let s = press(emptySelection, 1, 1)
    s = moveFocus(s, 1, 0, false, 5, 4)
    expect(cells(s, 5, 4)).toEqual(['2,1'])
    s = moveFocus(s, 1, 1, true, 5, 4)
    expect(cells(s, 5, 4)).toEqual(['2,1', '2,2', '3,1', '3,2'])
  })

  it('setas não saem da grade', () => {
    let s = press(emptySelection, 0, 0)
    s = moveFocus(s, -1, -1, false, 3, 3)
    expect(cells(s, 3, 3)).toEqual(['0,0'])
  })
})

describe('selectedCount', () => {
  it('não conta duas vezes células cobertas por blocos que se sobrepõem', () => {
    let s = press(emptySelection, 0, 0)
    s = dragTo(s, { r: 2, c: 2 })
    s = pressCell(s, { r: 1, c: 1 }, ctrl).sel // dentro do bloco: tira
    expect(selectedCount(s)).toBe(8)
    s = press(s, 1, 1, ctrl) // volta
    expect(selectedCount(s)).toBe(9)
  })
})
