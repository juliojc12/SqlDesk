/**
 * Seleção de células da grade, no estilo do DBeaver: um clique seleciona uma célula; Shift estende um bloco; Ctrl soma
 * ou tira células soltas, blocos, linhas e colunas. Posições são de exibição (já com ordenação e reordenação aplicadas).
 *
 * Representação: uma lista de retângulos mais um conjunto de células "excluídas" (as que o Ctrl+clique tirou de dentro de um
 * retângulo). Assim selecionar uma coluna de 100 mil linhas custa um retângulo, não 100 mil células.
 */

export interface Pos {
  r: number
  c: number
}

export interface Rect {
  r0: number
  r1: number
  c0: number
  c1: number
}

export interface Selection {
  rects: readonly Rect[]
  /** Células dentro de algum retângulo que ficaram de fora (chave: `cellKey`). */
  excluded: ReadonlySet<number>
  /** Ponto de partida do bloco em edição (Shift e arrastar estendem a partir dele). */
  anchor: Pos | null
  focus: Pos | null
}

export interface Mods {
  ctrl: boolean
  shift: boolean
}

export const emptySelection: Selection = { rects: [], excluded: new Set(), anchor: null, focus: null }

const cellKey = (r: number, c: number) => r * 65536 + c
const keyRow = (k: number) => Math.floor(k / 65536)
const keyCol = (k: number) => k % 65536

export function rectOf(a: Pos, b: Pos): Rect {
  return { r0: Math.min(a.r, b.r), r1: Math.max(a.r, b.r), c0: Math.min(a.c, b.c), c1: Math.max(a.c, b.c) }
}

const inRect = (rect: Rect, r: number, c: number) => r >= rect.r0 && r <= rect.r1 && c >= rect.c0 && c <= rect.c1

export function isSelected(sel: Selection, r: number, c: number): boolean {
  if (sel.excluded.has(cellKey(r, c))) return false
  for (const rect of sel.rects) if (inRect(rect, r, c)) return true
  return false
}

export const hasSelection = (sel: Selection) => sel.rects.length > 0

/** O que foi marcado de novo vale mais do que o que o Ctrl+clique tinha tirado antes: tira as exclusões que o retângulo cobre. */
function withoutExcludedIn(excluded: ReadonlySet<number>, rect: Rect): Set<number> {
  const next = new Set<number>()
  for (const k of excluded) if (!inRect(rect, keyRow(k), keyCol(k))) next.add(k)
  return next
}

const replaceLast = (rects: readonly Rect[], rect: Rect): Rect[] => (rects.length === 0 ? [rect] : [...rects.slice(0, -1), rect])

/**
 * Botão do mouse apertado numa célula. `dragging` diz se arrastar a partir daqui deve estender o bloco
 * (não deve, quando o Ctrl+clique apenas tirou uma célula da seleção).
 */
export function pressCell(sel: Selection, pos: Pos, m: Mods): { sel: Selection; dragging: boolean } {
  if (m.shift && sel.anchor) {
    const rect = rectOf(sel.anchor, pos)
    const rects = m.ctrl ? replaceLast(sel.rects, rect) : [rect]
    const excluded = withoutExcludedIn(m.ctrl ? sel.excluded : new Set(), rect)
    return { sel: { rects, excluded, anchor: sel.anchor, focus: pos }, dragging: true }
  }
  if (m.ctrl) {
    if (isSelected(sel, pos.r, pos.c)) {
      const excluded = new Set(sel.excluded).add(cellKey(pos.r, pos.c))
      return { sel: { rects: sel.rects, excluded, anchor: pos, focus: pos }, dragging: false }
    }
    const rect = rectOf(pos, pos)
    return { sel: { rects: [...sel.rects, rect], excluded: withoutExcludedIn(sel.excluded, rect), anchor: pos, focus: pos }, dragging: true }
  }
  return { sel: { rects: [rectOf(pos, pos)], excluded: new Set(), anchor: pos, focus: pos }, dragging: true }
}

/** Arrastando com o botão apertado: o último bloco vai do ponto de partida até a célula sob o mouse. */
export function dragTo(sel: Selection, pos: Pos): Selection {
  if (!sel.anchor || sel.rects.length === 0) return sel
  const rect = rectOf(sel.anchor, pos)
  return { ...sel, rects: replaceLast(sel.rects, rect), excluded: withoutExcludedIn(sel.excluded, rect), focus: pos }
}

const sameRect = (a: Rect, b: Rect) => a.r0 === b.r0 && a.r1 === b.r1 && a.c0 === b.c0 && a.c1 === b.c1

/** Soma um bloco inteiro (linha ou coluna); com Ctrl num bloco que já é exatamente igual, tira-o da seleção. */
function pressBlock(sel: Selection, rect: Rect, anchor: Pos, focus: Pos, m: Mods): Selection {
  if (m.ctrl) {
    const existing = sel.rects.findIndex((x) => sameRect(x, rect))
    if (existing >= 0) return { ...sel, rects: sel.rects.filter((_, i) => i !== existing) }
    return { rects: [...sel.rects, rect], excluded: withoutExcludedIn(sel.excluded, rect), anchor, focus }
  }
  return { rects: [rect], excluded: new Set(), anchor, focus }
}

/** Estende o último bloco (Shift) ou, com Ctrl+Shift, o último bloco mantendo os anteriores. */
function extendBlock(sel: Selection, rect: Rect, focus: Pos, m: Mods): Selection {
  const keep = m.ctrl ? sel.rects.slice(0, -1) : []
  return { rects: [...keep, rect], excluded: withoutExcludedIn(m.ctrl ? sel.excluded : new Set(), rect), anchor: sel.anchor, focus }
}

/** Clique no número da linha: seleciona a linha toda; Shift estende de linha em linha; Ctrl soma ou tira a linha. */
export function pressRow(sel: Selection, r: number, lastCol: number, m: Mods): Selection {
  const extend = m.shift && sel.anchor !== null
  const from = extend ? (sel.anchor as Pos).r : r
  const rect: Rect = { r0: Math.min(from, r), r1: Math.max(from, r), c0: 0, c1: lastCol }
  const focus = { r, c: lastCol }
  if (extend) return extendBlock(sel, rect, focus, m)
  return pressBlock(sel, rect, { r, c: 0 }, focus, m)
}

/** Clique no título da coluna: seleciona a coluna toda; Shift estende de coluna em coluna; Ctrl soma ou tira a coluna. */
export function pressColumn(sel: Selection, c: number, total: number, m: Mods): Selection {
  if (total === 0) return sel
  const extend = m.shift && sel.anchor !== null
  const from = extend ? (sel.anchor as Pos).c : c
  const rect: Rect = { r0: 0, r1: total - 1, c0: Math.min(from, c), c1: Math.max(from, c) }
  const focus = { r: total - 1, c }
  if (extend) return extendBlock({ ...sel, anchor: { r: 0, c: from } }, rect, focus, m)
  return pressBlock(sel, rect, { r: 0, c }, focus, m)
}

export function selectAll(total: number, cols: number): Selection {
  if (total === 0 || cols === 0) return emptySelection
  return { rects: [{ r0: 0, r1: total - 1, c0: 0, c1: cols - 1 }], excluded: new Set(), anchor: { r: 0, c: 0 }, focus: { r: total - 1, c: cols - 1 } }
}

/** Setas do teclado: movem a célula ativa; com Shift, estendem o último bloco. */
export function moveFocus(sel: Selection, dr: number, dc: number, shift: boolean, total: number, cols: number): Selection {
  if (!sel.focus || total === 0) return sel
  const focus = { r: Math.min(total - 1, Math.max(0, sel.focus.r + dr)), c: Math.min(cols - 1, Math.max(0, sel.focus.c + dc)) }
  if (shift && sel.anchor) {
    const rect = rectOf(sel.anchor, focus)
    return { ...sel, rects: replaceLast(sel.rects, rect), excluded: withoutExcludedIn(sel.excluded, rect), focus }
  }
  return { rects: [rectOf(focus, focus)], excluded: new Set(), anchor: focus, focus }
}

/** A coluna inteira está selecionada (todas as linhas, nenhuma célula tirada)? Usado para destacar o título. */
export function isColumnSelected(sel: Selection, c: number, total: number): boolean {
  if (total === 0 || !sel.rects.some((x) => x.r0 === 0 && x.r1 >= total - 1 && c >= x.c0 && c <= x.c1)) return false
  for (const k of sel.excluded) if (keyCol(k) === c) return false
  return true
}

/** Faixa de linhas que contém alguma seleção, ou null. */
export function rowBounds(sel: Selection): { r0: number; r1: number } | null {
  if (sel.rects.length === 0) return null
  return { r0: Math.min(...sel.rects.map((x) => x.r0)), r1: Math.max(...sel.rects.map((x) => x.r1)) }
}

/** Quantidade de células selecionadas (para mostrar no rodapé). */
export function selectedCount(sel: Selection): number {
  // Soma por linha para não contar duas vezes células cobertas por retângulos que se sobrepõem.
  const b = rowBounds(sel)
  if (!b) return 0
  const cols = new Set<number>()
  for (const x of sel.rects) for (let c = x.c0; c <= x.c1; c++) cols.add(c)
  let n = 0
  for (let r = b.r0; r <= b.r1; r++) for (const c of cols) if (isSelected(sel, r, c)) n++
  return n
}
