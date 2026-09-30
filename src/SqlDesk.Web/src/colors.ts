export const PALETTE = [
  '#E74856', '#F7630C', '#FFB900', '#8CBD18', '#10893E', '#00B7C3',
  '#0078D4', '#4F6BED', '#8764B8', '#C239B3', '#E3008C', '#7A7574',
] as const

/** Cor usada quando a aba não tem conexão. */
export const NEUTRAL_COLOR = '#7A7574'

function channel(v: number): number {
  const c = v / 255
  return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
}

function rgb(hex: string): [number, number, number] {
  const n = parseInt(hex.slice(1), 16)
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
}

function toHex([r, g, b]: [number, number, number]): string {
  return '#' + [r, g, b].map((v) => Math.round(v).toString(16).padStart(2, '0')).join('').toUpperCase()
}

/** Luminância relativa (WCAG 2.x) de #RRGGBB. */
export function luminance(hex: string): number {
  const [r, g, b] = rgb(hex)
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b)
}

export function contrastRatio(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x)
  return (hi + 0.05) / (lo + 0.05)
}

/** Preto ou branco: o que tiver maior contraste sobre o fundo. */
export function textOn(bg: string): '#000000' | '#FFFFFF' {
  return contrastRatio(bg, '#FFFFFF') >= contrastRatio(bg, '#000000') ? '#FFFFFF' : '#000000'
}

/** Mistura `a` com `b`; t = 0 devolve `a`, t = 1 devolve `b`. */
export function mix(a: string, b: string, t: number): string {
  const [ra, rb] = [rgb(a), rgb(b)]
  return toHex([0, 1, 2].map((i) => ra[i] + (rb[i] - ra[i]) * t) as [number, number, number])
}

/** Fundo claro tingido pela cor da conexão e um texto da mesma família, com contraste >= 4,5:1. */
export function tintedSurface(color: string): { bg: string; fg: string } {
  const bg = mix(color, '#FFFFFF', 0.88)
  let fg = color
  for (let t = 0; contrastRatio(fg, bg) < 4.5 && t <= 1; t += 0.05) fg = mix(color, '#000000', t)
  return { bg, fg }
}
