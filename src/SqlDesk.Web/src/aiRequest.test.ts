import { describe, expect, it } from 'vitest'
import { commentOut, findRequest, resultText } from './aiRequest'

describe('findRequest', () => {
  const text = 'primeiro pedido\nsegunda linha\n\noutro pedido\n\n'

  it('usa a seleção quando existe', () => {
    expect(findRequest(text, 0, 0, 8)).toEqual({ start: 0, end: 8, text: 'primeiro' })
    expect(findRequest(text, 0, 8, 0)).toEqual({ start: 0, end: 8, text: 'primeiro' })
  })

  it('seleção só de espaços não vale', () => {
    expect(findRequest('a   b', 2, 1, 4)).toBeNull()
  })

  it('sem seleção, pega o parágrafo sob o cursor', () => {
    const r = findRequest(text, 3, 3, 3)
    expect(r?.text).toBe('primeiro pedido\nsegunda linha')
    const r2 = findRequest(text, text.indexOf('outro') + 2, text.indexOf('outro') + 2, text.indexOf('outro') + 2)
    expect(r2?.text).toBe('outro pedido')
  })

  it('cursor no fim do parágrafo ainda o encontra', () => {
    const end = 'primeiro pedido\nsegunda linha'.length
    expect(findRequest(text, end, end, end)?.text).toBe('primeiro pedido\nsegunda linha')
  })

  it('linha em branco ou texto vazio não tem pedido', () => {
    expect(findRequest(text, text.indexOf('\n\n') + 1, text.indexOf('\n\n') + 1, text.indexOf('\n\n') + 1)).toBeNull()
    expect(findRequest('', 0, 0, 0)).toBeNull()
  })

  it('entende CRLF sem levar o \\r para o pedido', () => {
    const t = 'a\r\nb\r\n\r\nc'
    const r = findRequest(t, 0, 0, 0)
    expect(r?.text).toBe('a\r\nb')
    expect(t.slice(r!.start, r!.end)).toBe(r!.text)
  })
})

describe('commentOut / resultText', () => {
  it('comenta cada linha e põe o aviso', () => {
    expect(commentOut('UPDATE t\nSET a = 1', 'não começa com SELECT')).toBe(
      '-- ⚠ Não é somente leitura (não começa com SELECT). Revise antes de usar.\n-- UPDATE t\n-- SET a = 1',
    )
  })

  it('"*/" dentro do SQL não escapa do comentário', () => {
    const out = commentOut('SELECT 1 /* x */ ; DELETE FROM t', null)
    expect(out.split('\n').every((l) => l.startsWith('--'))).toBe(true)
  })

  it('linha vazia vira "--" sem espaço sobrando', () => {
    expect(commentOut('a\n\nb', null).split('\n')).toEqual([expect.any(String), '-- a', '--', '-- b'])
  })

  it('somente leitura entra como veio; o resto, comentado', () => {
    expect(resultText(' SELECT 1 ', true, null)).toBe('SELECT 1')
    expect(resultText('DELETE FROM t', false, 'x').startsWith('-- ⚠')).toBe(true)
  })
})
