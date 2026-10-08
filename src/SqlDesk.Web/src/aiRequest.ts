/**
 * Consulta com IA: quais linhas do editor são o pedido e como o SQL gerado volta ao texto. Funções puras (o Monaco fica em
 * `editorActions.ts`). O SQL que a trava do backend reprova nunca entra "solto": entra comentado linha a linha, para um
 * Ctrl+Enter acidental não executar nada.
 */

export interface AiRequestSpan {
  start: number
  end: number
  text: string
}

/**
 * O pedido é a seleção; sem seleção, o parágrafo sob o cursor (linhas seguidas não vazias). Devolve offsets no texto
 * original (o fim exclui a quebra de linha) ou null se não houver nada escrito ali.
 */
export function findRequest(text: string, cursor: number, selectionStart: number, selectionEnd: number): AiRequestSpan | null {
  const a = Math.min(selectionStart, selectionEnd)
  const b = Math.max(selectionStart, selectionEnd)
  if (b > a) {
    const sel = text.slice(a, b)
    return sel.trim() === '' ? null : { start: a, end: b, text: sel }
  }

  // Linhas com offsets; `\r\n` conta como uma quebra.
  const lines: { start: number; end: number }[] = []
  let pos = 0
  while (pos <= text.length) {
    let eol = text.indexOf('\n', pos)
    if (eol < 0) eol = text.length
    const end = eol > pos && text[eol - 1] === '\r' ? eol - 1 : eol
    lines.push({ start: pos, end })
    pos = eol + 1
  }

  const blank = (i: number) => text.slice(lines[i].start, lines[i].end).trim() === ''
  // A linha do cursor é a última que começa nele ou antes (o cursor no fim da linha ainda é dela).
  let at = 0
  for (let i = 0; i < lines.length; i++) if (lines[i].start <= cursor) at = i
  if (blank(at)) return null

  let first = at
  let last = at
  while (first > 0 && !blank(first - 1)) first--
  while (last < lines.length - 1 && !blank(last + 1)) last++
  const start = lines[first].start
  const end = lines[last].end
  return { start, end, text: text.slice(start, end) }
}

/** Comenta o SQL linha a linha (`-- `), com um cabeçalho dizendo por que não pode rodar direto. */
export function commentOut(sql: string, reason: string | null | undefined): string {
  const why = reason ? ` (${reason})` : ''
  const header = `-- ⚠ Não é somente leitura${why}. Revise antes de usar.`
  const body = sql.replace(/\r\n/g, '\n').split('\n').map((l) => (l.trim() === '' ? '--' : `-- ${l}`))
  return [header, ...body].join('\n')
}

/** Texto que entra no editor no lugar do pedido. */
export function resultText(sql: string, readOnly: boolean, reason: string | null | undefined): string {
  return readOnly ? sql.trim() : commentOut(sql.trim(), reason)
}
