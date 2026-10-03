/**
 * Análise leve do T-SQL para o autocomplete: não depende de o statement estar completo nem parseável. O parse de
 * verdade (travas, statement sob o cursor) fica no backend; aqui só se descobre o contexto sob o cursor.
 */
import { unquote } from './alias'
import { aliasReservedWords, type ProviderId } from './providers'

type ScanState = 'code' | 'line-comment' | 'block-comment' | 'string'

/**
 * Troca comentários e o conteúdo de strings por espaços (mantendo tamanho e quebras de linha), para que palavras
 * dentro deles não confundam a análise. Também informa se `upto` cai dentro de um comentário ou string.
 *
 * No MySQL: `#` e `-- ` (com espaço) são comentários de linha, comentário de bloco não aninha, strings aceitam aspas
 * simples ou duplas e o escape com barra invertida, e identificadores vão entre crases (no lugar dos colchetes).
 */
export function scan(text: string, upto = text.length, provider: ProviderId = 'sqlserver'): { masked: string; inside: boolean } {
  const mysql = provider === 'mysql'
  const out: string[] = []
  let state: ScanState = 'code'
  let depth = 0
  let quote = "'"
  let insideAtUpto = false

  for (let i = 0; i < text.length; i++) {
    if (i === upto) insideAtUpto = state !== 'code'
    const ch = text[i]
    const next = text[i + 1]
    const blank = ch === '\n' || ch === '\r' ? ch : ' '

    switch (state) {
      case 'code':
        if (ch === '-' && next === '-' && (!mysql || i + 2 >= text.length || /\s/.test(text[i + 2]))) {
          state = 'line-comment'
          out.push(' ')
        } else if (mysql && ch === '#') {
          state = 'line-comment'
          out.push(' ')
        } else if (ch === '/' && next === '*') {
          state = 'block-comment'
          depth = 1
          out.push(' ', ' ')
          i++
        } else if (ch === "'" || (mysql && ch === '"')) {
          state = 'string'
          quote = ch
          out.push(' ')
        } else if (mysql && ch === '`') {
          // identificador entre crases: mantém (aceita "``" como escape)
          let j = i + 1
          while (j < text.length && !(text[j] === '`' && text[j + 1] !== '`')) j += text[j] === '`' ? 2 : 1
          const end = Math.min(j, text.length - 1)
          out.push(text.slice(i, end + 1))
          i = end
        } else if (!mysql && ch === '[') {
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
        if (!mysql && ch === '/' && next === '*') {
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
        if ((ch === quote && next === quote) || (mysql && ch === '\\' && i + 1 < text.length)) {
          // aspas dobradas, ou (MySQL) barra invertida seguida de qualquer caractere: o par continua dentro da string
          if (mysql && i + 1 === upto) insideAtUpto = true
          out.push(' ', next === '\n' || next === '\r' ? next : ' ')
          i++
        } else if (ch === quote) {
          state = 'code'
          out.push(' ')
        } else out.push(blank)
        break
    }
  }
  if (upto >= text.length) insideAtUpto = state !== 'code'
  return { masked: out.join(''), inside: insideAtUpto }
}

/** Limites do statement que contém o cursor: separado por `;`, por linha só com `GO` (não no MySQL) ou por linha em branco. */
export function statementBounds(masked: string, cursor: number, provider: ProviderId = 'sqlserver'): { start: number; end: number } {
  const sep = provider === 'mysql' ? /;|\r?\n[ \t]*\r?\n/gm : /;|^[ \t]*GO[ \t]*\r?$|\r?\n[ \t]*\r?\n/gim
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

// Nome: [colchetes] e "aspas" (SQL Server), `crases` (MySQL, com `` como escape) ou sem delimitador.
const IDENT = String.raw`(?:\[[^\]]*\]|"[^"]*"|` + '`(?:[^`]|``)*`' + String.raw`|[A-Za-z_@#][\w@#$]*)`
const QNAME = String.raw`${IDENT}(?:\s*\.\s*${IDENT}){0,2}`
// Alias opcional depois do nome da tabela; palavras reservadas (WHERE, JOIN, ON...) nunca são consumidas como alias.
const aliasPattern = (words: ReadonlySet<string>) => String.raw`(?:\s+(?:AS\s+)?(?!(?:${[...words].join('|')})\b)(${IDENT}))?`
const ALIAS: Record<ProviderId, string> = { sqlserver: aliasPattern(aliasReservedWords('sqlserver')), mysql: aliasPattern(aliasReservedWords('mysql')) }

export interface TableRef {
  schema?: string
  name: string
  alias?: string
}

const splitName = (q: string): string[] => (q.match(new RegExp(IDENT, 'g')) ?? []).map(unquote)

function toRef(qname: string, alias: string | undefined, reserved: ReadonlySet<string>): TableRef {
  const parts = splitName(qname)
  const name = parts.at(-1) ?? qname
  const schema = parts.length >= 2 ? parts.at(-2) : undefined
  const a = alias && !reserved.has(unquote(alias).toUpperCase()) ? unquote(alias) : undefined
  return { schema, name, alias: a }
}

/** Tabelas citadas no statement (FROM, JOIN, UPDATE, INTO, USING e listas separadas por vírgula), com seus aliases. */
export function parseTableRefs(maskedStatement: string, provider: ProviderId = 'sqlserver'): TableRef[] {
  const refs: TableRef[] = []
  const reserved = aliasReservedWords(provider)
  const alias = ALIAS[provider]
  const head = new RegExp(String.raw`\b(?:FROM|JOIN|UPDATE|INTO|USING)\s+(${QNAME})${alias}`, 'gi')
  for (let m = head.exec(maskedStatement); m; m = head.exec(maskedStatement)) {
    refs.push(toRef(m[1], m[2], reserved))
    // FROM a x, b y, c z
    const more = new RegExp(String.raw`\s*,\s*(${QNAME})${alias}`, 'yi')
    for (;;) {
      more.lastIndex = head.lastIndex
      const c = more.exec(maskedStatement)
      if (!c) break
      refs.push(toRef(c[1], c[2], reserved))
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

/** Contexto do cursor, a partir do texto do statement até ele (já sem comentários e strings). No MySQL, CALL lista procedures. */
export function detectContext(before: string, provider: ProviderId = 'sqlserver'): Context {
  const partial = /[\w@#$]*$/.exec(before)?.[0] ?? ''
  const head = before.slice(0, before.length - partial.length)

  const qualified = new RegExp(String.raw`(${IDENT}(?:\s*\.\s*${IDENT})*)\s*\.\s*$`).exec(head)
  if (qualified) return { kind: 'qualified', parts: splitName(qualified[1]) }

  const tokens = head.match(/\[[^\]]*\]|"[^"]*"|`(?:[^`]|``)*`|[A-Za-z_@#][\w@#$]*|\d+|[(),.=<>!+\-*/%;]/g) ?? []
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
    if (up === 'EXEC' || up === 'EXECUTE' || (provider === 'mysql' && up === 'CALL')) return after.length === 0 ? { kind: 'exec' } : { kind: 'other' }
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

export function analyze(text: string, offset: number, provider: ProviderId = 'sqlserver'): Analysis {
  const { masked, inside } = scan(text, offset, provider)
  if (inside) return { context: { kind: 'other' }, refs: [], after: '', inside: true }
  const { start, end } = statementBounds(masked, offset, provider)
  const stmt = masked.slice(start, end)
  return {
    context: detectContext(masked.slice(start, offset), provider),
    refs: parseTableRefs(stmt, provider),
    after: masked.slice(offset, end),
    inside: false,
  }
}
