/**
 * Análise leve do T-SQL para o autocomplete: não depende de o statement estar completo nem parseável. O parse de
 * verdade (travas, statement sob o cursor) fica no backend; aqui só se descobre o contexto sob o cursor.
 */
import { RESERVED_WORDS, unquote } from './alias'

type ScanState = 'code' | 'line-comment' | 'block-comment' | 'string'

/**
 * Troca comentários e o conteúdo de strings por espaços (mantendo tamanho e quebras de linha), para que palavras
 * dentro deles não confundam a análise. Também informa se `upto` cai dentro de um comentário ou string.
 */
export function scan(text: string, upto = text.length): { masked: string; inside: boolean } {
  const out: string[] = []
  let state: ScanState = 'code'
  let depth = 0
  let insideAtUpto = false

  for (let i = 0; i < text.length; i++) {
    if (i === upto) insideAtUpto = state !== 'code'
    const ch = text[i]
    const next = text[i + 1]
    const blank = ch === '\n' || ch === '\r' ? ch : ' '

    switch (state) {
      case 'code':
        if (ch === '-' && next === '-') {
          state = 'line-comment'
          out.push(' ')
        } else if (ch === '/' && next === '*') {
          state = 'block-comment'
          depth = 1
          out.push(' ', ' ')
          i++
        } else if (ch === "'") {
          state = 'string'
          out.push(' ')
        } else if (ch === '[') {
          // identificador entre colchetes: mantém (aceita "]]" como escape)
          let j = i + 1
          while (j < text.length && !(text[j] === ']' && text[j + 1] !== ']')) j += text[j] === ']' ? 2 : 1
          const end = Math.min(j, text.length - 1)
          out.push(text.slice(i, end + 1))
          i = end
        } else out.push(ch)
        break
      case 'line-comment':
        if (ch === '\n') {
          state = 'code'
          out.push(ch)
        } else out.push(blank)
        break
      case 'block-comment':
        if (ch === '/' && next === '*') {
          depth++
          out.push(' ', ' ')
          i++
        } else if (ch === '*' && next === '/') {
          depth--
          out.push(' ', ' ')
          i++
          if (depth === 0) state = 'code'
        } else out.push(blank)
        break
      case 'string':
        if (ch === "'" && next === "'") {
          out.push(' ', ' ')
          i++
        } else if (ch === "'") {
          state = 'code'
          out.push(' ')
        } else out.push(blank)
        break
    }
  }
  if (upto >= text.length) insideAtUpto = state !== 'code'
  return { masked: out.join(''), inside: insideAtUpto }
}

/** Limites do statement que contém o cursor: separado por `;`, por linha só com `GO` ou por linha em branco. */
export function statementBounds(masked: string, cursor: number): { start: number; end: number } {
  const sep = /;|^[ \t]*GO[ \t]*\r?$|\r?\n[ \t]*\r?\n/gim
  let start = 0
  let end = masked.length
  for (let m = sep.exec(masked); m; m = sep.exec(masked)) {
    const mStart = m.index
    const mEnd = m.index + m[0].length
    if (mEnd <= cursor) start = mEnd
    else if (mStart >= cursor) {
      end = mStart
      break
    }
  }
  return { start, end }
}

const IDENT = String.raw`(?:\[[^\]]*\]|"[^"]*"|[A-Za-z_@#][\w@#$]*)`
const QNAME = String.raw`${IDENT}(?:\s*\.\s*${IDENT}){0,2}`
// Alias opcional depois do nome da tabela; palavras reservadas (WHERE, JOIN, ON...) nunca são consumidas como alias.
const ALIAS = String.raw`(?:\s+(?:AS\s+)?(?!(?:${[...RESERVED_WORDS].join('|')})\b)(${IDENT}))?`

export interface TableRef {
  schema?: string
  name: string
  alias?: string
}

const splitName = (q: string): string[] => (q.match(new RegExp(IDENT, 'g')) ?? []).map(unquote)

function toRef(qname: string, alias?: string): TableRef {
  const parts = splitName(qname)
  const name = parts.at(-1) ?? qname
  const schema = parts.length >= 2 ? parts.at(-2) : undefined
  const a = alias && !RESERVED_WORDS.has(unquote(alias).toUpperCase()) ? unquote(alias) : undefined
  return { schema, name, alias: a }
}

/** Tabelas citadas no statement (FROM, JOIN, UPDATE, INTO, USING e listas separadas por vírgula), com seus aliases. */
export function parseTableRefs(maskedStatement: string): TableRef[] {
  const refs: TableRef[] = []
  const head = new RegExp(String.raw`\b(?:FROM|JOIN|UPDATE|INTO|USING)\s+(${QNAME})${ALIAS}`, 'gi')
  for (let m = head.exec(maskedStatement); m; m = head.exec(maskedStatement)) {
    refs.push(toRef(m[1], m[2]))
    // FROM a x, b y, c z
    const more = new RegExp(String.raw`\s*,\s*(${QNAME})${ALIAS}`, 'yi')
    for (;;) {
      more.lastIndex = head.lastIndex
      const c = more.exec(maskedStatement)
      if (!c) break
      refs.push(toRef(c[1], c[2]))
      head.lastIndex = more.lastIndex
    }
  }
  return refs
}

export type TableKeyword = 'FROM' | 'JOIN' | 'UPDATE' | 'INTO' | 'DELETE FROM' | 'TABLE' | 'USING'

export type Context =
  | { kind: 'qualified'; parts: string[] }
  | { kind: 'table'; keyword: TableKeyword }
  | { kind: 'columns' }
  | { kind: 'exec' }
  | { kind: 'other' }

const COLUMN_KEYWORDS = new Set(['SELECT', 'WHERE', 'ON', 'HAVING', 'SET', 'BY', 'AND', 'OR', 'WHEN', 'THEN', 'ELSE', 'CASE', 'DISTINCT', 'OUTPUT'])

/** Contexto do cursor, a partir do texto do statement até ele (já sem comentários e strings). */
export function detectContext(before: string): Context {
  const partial = /[\w@#$]*$/.exec(before)?.[0] ?? ''
  const head = before.slice(0, before.length - partial.length)

  const qualified = new RegExp(String.raw`(${IDENT}(?:\s*\.\s*${IDENT})*)\s*\.\s*$`).exec(head)
  if (qualified) return { kind: 'qualified', parts: splitName(qualified[1]) }

  const tokens = head.match(/\[[^\]]*\]|"[^"]*"|[A-Za-z_@#][\w@#$]*|\d+|[(),.=<>!+\-*/%;]/g) ?? []
  let depth = 0
  for (let i = tokens.length - 1; i >= 0; i--) {
    const t = tokens[i]
    if (t === ')') {
      depth++
      continue
    }
    if (t === '(') {
      if (depth > 0) depth--
      continue
    }
    if (depth > 0) continue
    if (t === ';') return { kind: 'other' }

    const up = t.toUpperCase()
    const after = tokens.slice(i + 1)
    if (up === 'FROM') {
      const keyword: TableKeyword = tokens[i - 1]?.toUpperCase() === 'DELETE' ? 'DELETE FROM' : 'FROM'
      return after.length === 0 || after.at(-1) === ',' ? { kind: 'table', keyword } : { kind: 'other' }
    }
    if (up === 'JOIN' || up === 'UPDATE' || up === 'USING' || up === 'TABLE') {
      return after.length === 0 ? { kind: 'table', keyword: up } : { kind: 'other' }
    }
    if (up === 'INTO') {
      if (after.length === 0) return { kind: 'table', keyword: 'INTO' }
      return after.includes('(') ? { kind: 'columns' } : { kind: 'other' }
    }
    if (up === 'EXEC' || up === 'EXECUTE') return after.length === 0 ? { kind: 'exec' } : { kind: 'other' }
    if (COLUMN_KEYWORDS.has(up)) return { kind: 'columns' }
  }
  return { kind: 'other' }
}

export interface Analysis {
  context: Context
  refs: TableRef[]
  /** Texto (sem comentários e strings) do cursor até o fim do statement. */
  after: string
  inside: boolean
}

export function analyze(text: string, offset: number): Analysis {
  const { masked, inside } = scan(text, offset)
  if (inside) return { context: { kind: 'other' }, refs: [], after: '', inside: true }
  const { start, end } = statementBounds(masked, offset)
  const stmt = masked.slice(start, end)
  return {
    context: detectContext(masked.slice(start, offset)),
    refs: parseTableRefs(stmt),
    after: masked.slice(offset, end),
    inside: false,
  }
}
