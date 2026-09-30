import { describe, expect, it } from 'vitest'
import { contrastRatio, mix, NEUTRAL_COLOR, PALETTE, textOn, tintedSurface } from './colors'

describe('colors', () => {
  it('textOn garante contraste >= 4,5:1 para toda a paleta', () => {
    for (const c of [...PALETTE, '#000000', '#FFFFFF', '#808080']) {
      expect(contrastRatio(c, textOn(c)), c).toBeGreaterThanOrEqual(4.5)
    }
  })

  it('escolhe texto claro sobre fundo escuro e escuro sobre fundo claro', () => {
    expect(textOn('#1B3A6B')).toBe('#FFFFFF')
    expect(textOn('#FFB900')).toBe('#000000')
  })

  it('tintedSurface mantém contraste >= 4,5:1 para toda a paleta', () => {
    for (const c of [...PALETTE, NEUTRAL_COLOR]) {
      const { bg, fg } = tintedSurface(c)
      expect(contrastRatio(fg, bg), c).toBeGreaterThanOrEqual(4.5)
    }
  })

  it('mix interpola', () => {
    expect(mix('#000000', '#FFFFFF', 0)).toBe('#000000')
    expect(mix('#000000', '#FFFFFF', 1)).toBe('#FFFFFF')
    expect(mix('#000000', '#FFFFFF', 0.5)).toBe('#808080')
  })
})
