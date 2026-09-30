export const PALETTE = [
  '#E74856', '#F7630C', '#FFB900', '#8CBD18', '#10893E', '#00B7C3',
  '#0078D4', '#4F6BED', '#8764B8', '#C239B3', '#E3008C', '#7A7574',
] as const

function channel(v: number): number {
  const c = v / 255
  return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
}

/** Luminância relativa (WCAG 2.x) de #RRGGBB. */
export function luminance(hex: string): number {
  const n = parseInt(hex.slice(1), 16)
  return 0.2126 * channel((n >> 16) & 255) + 0.7152 * channel((n >> 8) & 255) + 0.0722 * channel(n & 255)
}

export function contrastRatio(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x)
  return (hi + 0.05) / (lo + 0.05)
}

/** Escolhe preto ou branco, o que tiver maior contraste sobre o fundo (sempre >= 4,5:1 na prática). */
export function textOn(bg: string): '#000000' | '#FFFFFF' {
  return contrastRatio(bg, '#FFFFFF') >= contrastRatio(bg, '#000000') ? '#FFFFFF' : '#000000'
}
