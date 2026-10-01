import { format as sqlFormat } from 'sql-formatter'
import { RESERVED_WORDS } from './alias'
import { scan } from './sqlContext'
import { KEYWORDS } from './sqlKeywords'

export type FormatResult = { ok: true; text: string } | { ok: false; reason: string }

type Formatter = (sql: string) => string

const defaultFormatter: Formatter = (sql) =>
  sqlFormat(sql, { language: 'transactsql', keywordCase: 'upper', tabWidth: 4, linesBetweenQueries: 1 })

/** Palavras que o formatador pode legitimamente trocar para maiúsculas. Qualquer outra mudança de caixa é recusada. */
const KEYWORD_SET: ReadonlySet<string> = new Set([
  ...RESERVED_WORDS,
  ...KEYWORDS.flatMap((k) => k.split(' ')),
  'TOP', 'NOLOCK', 'OUTPUT', 'OFFSET', 'FETCH', 'NEXT', 'FIRST', 'ROWS', 'ONLY', 'PERCENT', 'TIES', 'APPLY', 'OVER', 'PARTITION',
  'PIVOT', 'UNPIVOT', 'MATCHED', 'TARGET', 'SOURCE', 'OPTION', 'RECOMPILE', 'MAXDOP', 'READONLY', 'EXISTS', 'ALL', 'ANY', 'SOME',
  'INSERTED', 'DELETED', 'CTE', 'TRAN', 'TRANSACTION', 'COMMIT', 'ROLLBACK', 'SAVE', 'BEGIN', 'END', 'TRY', 'CATCH', 'THROW',
  'WHILE', 'BREAK', 'CONTINUE', 'RETURN', 'DECLARE', 'SET', 'EXEC', 'EXECUTE', 'PRINT', 'USE', 'GO', 'LIKE', 'ESCAPE', 'COLLATE',
])

const TOKEN = /--[^\n]*|\/\*[\s\S]*?\*\/|'(?:[^']|'')*'|\[[^\]]*\]|"[^"]*"|[A-Za-z_@#][\w@#$]*|\d[\w.]*|\S/g

/**
 * Compara o texto original com o formatado, token a token. O formatador só pode mudar espaços, quebras de linha, a indentação
 * dentro de comentários e a caixa de palavras-chave. Textos entre aspas, identificadores, números e símbolos precisam ser idênticos.
 */
export function sameContent(original: string, formatted: string): boolean {
  const a = original.match(TOKEN) ?? []
  const b = formatted.match(TOKEN) ?? []
  if (a.length !== b.length) return false
  return a.every((x, i) => {
    const y = b[i]
    if (x === y) return true
    if (x.startsWith('--') || x.startsWith('/*')) return y.startsWith(x.slice(0, 2)) && x.replace(/\s+/g, '') === y.replace(/\s+/g, '')
    return /^[A-Za-z]/.test(x) && y === x.toUpperCase() && KEYWORD_SET.has(y)
  })
}

/**
 * Formata um script T-SQL: palavras-chave em maiúsculas, uma cláusula por linha, indentação de 4 espaços.
 *
 * - `GO` não é T-SQL: cada batch é formatado separadamente e as linhas `GO` ficam onde estavam.
 * - Comentários e textos entre aspas são preservados (por isso não se usa o gerador do ScriptDom, que descarta comentários).
 * - Formatar nunca pode mudar o que o script faz: texto que termina dentro de uma string ou comentário aberto é recusado,
 *   e o resultado só é aceito se `sameContent` confirmar que só espaços e a caixa de palavras-chave mudaram. Qualquer dúvida
 *   cancela tudo e o texto fica como estava.
 */
export function formatSql(text: string, formatter: Formatter = defaultFormatter): FormatResult {
  if (text.trim() === '') return { ok: true, text }
  // A quebra de linha extra encerra um comentário de linha que termina o texto (isso é válido); string ou comentário de bloco abertos continuam dentro.
  if (scan(text + '\n').inside) return { ok: false, reason: 'há uma string ou um comentário sem fechar' }

  const lines = text.split(/\r?\n/)
  // Linhas "GO" são procuradas no texto sem comentários e strings (um GO dentro de /* */ ou de uma string não é separador).
  const masked = scan(lines.join('\n')).masked.split('\n')

  const out: string[] = []
  let chunk: string[] = []

  const flush = (): FormatResult | null => {
    const code = chunk.join('\n')
    chunk = []
    if (code.trim() === '') return null
    let formatted: string
    try {
      formatted = formatter(code).trim()
    } catch (e) {
      return { ok: false, reason: e instanceof Error ? e.message : String(e) }
    }
    if (!sameContent(code, formatted)) return { ok: false, reason: 'o resultado mudaria o conteúdo do script' }
    out.push(formatted)
    return null
  }

  for (let i = 0; i < lines.length; i++) {
    if (/^\s*GO(\s+\d+)?\s*$/i.test(masked[i])) {
      const failure = flush()
      if (failure) return failure
      out.push(lines[i].trim())
    } else {
      chunk.push(lines[i])
    }
  }
  const failure = flush()
  if (failure) return failure

  const endsWithNewline = /\n$/.test(text)
  return { ok: true, text: out.join('\n') + (endsWithNewline ? '\n' : '') }
}
