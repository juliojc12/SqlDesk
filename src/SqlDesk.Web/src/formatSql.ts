import { format as sqlFormat } from 'sql-formatter'
import { RESERVED_WORDS } from './alias'
import type { ProviderId } from './providers'
import { scan } from './sqlContext'
import { KEYWORDS } from './sqlKeywords'

export type FormatResult = { ok: true; text: string } | { ok: false; reason: string }

type Formatter = (sql: string) => string

/** Dialeto do sql-formatter de cada banco (o MySQL serve também para o MariaDB, nos comandos comuns). */
const FORMATTERS: Record<ProviderId, Formatter> = {
  sqlserver: (sql) => sqlFormat(sql, { language: 'transactsql', keywordCase: 'upper', tabWidth: 4, linesBetweenQueries: 1 }),
  mysql: (sql) => sqlFormat(sql, { language: 'mysql', keywordCase: 'upper', tabWidth: 4, linesBetweenQueries: 1 }),
}

/** Palavras que o formatador pode legitimamente trocar para maiúsculas. Qualquer outra mudança de caixa é recusada. */
const KEYWORD_SET: ReadonlySet<string> = new Set([
  ...RESERVED_WORDS,
  ...KEYWORDS.flatMap((k) => k.split(' ')),
  'TOP', 'NOLOCK', 'OUTPUT', 'OFFSET', 'FETCH', 'NEXT', 'FIRST', 'ROWS', 'ONLY', 'PERCENT', 'TIES', 'APPLY', 'OVER', 'PARTITION',
  'PIVOT', 'UNPIVOT', 'MATCHED', 'TARGET', 'SOURCE', 'OPTION', 'RECOMPILE', 'MAXDOP', 'READONLY', 'EXISTS', 'ALL', 'ANY', 'SOME',
  'INSERTED', 'DELETED', 'CTE', 'TRAN', 'TRANSACTION', 'COMMIT', 'ROLLBACK', 'SAVE', 'BEGIN', 'END', 'TRY', 'CATCH', 'THROW',
  'WHILE', 'BREAK', 'CONTINUE', 'RETURN', 'DECLARE', 'SET', 'EXEC', 'EXECUTE', 'PRINT', 'USE', 'GO', 'LIKE', 'ESCAPE', 'COLLATE',
])

/**
 * MySQL: nomes de tabela, banco e alias de tabela diferenciam maiúsculas (Linux, lower_case_table_names=0), então trocar a caixa
 * de um nome muda o alvo do comando. Palavra reservada nunca é nome sem crases (salvo depois de ponto, tratado à parte) e pode
 * ir para maiúsculas. Palavra não reservada (OFFSET, COMMIT, END...) pode ser nome de tabela: só vai para maiúsculas na posição
 * em que não há como ser nome. Fora destas regras, a formatação é recusada.
 */
type Ctx = { prev?: string; prev2?: string; next?: string; caseOpen: boolean }

/**
 * Palavras que podem ir para maiúsculas em qualquer posição (fora do vizinho de ponto): reservadas em TODAS as versões aceitas
 * (MySQL 5.7 e 8.0, MariaDB 10 e 11), logo nunca são nome sem crases em nenhum servidor. Palavras reservadas só em algumas
 * versões (GROUPS, SYSTEM, CUBE, LATERAL, RECURSIVE, ROWS, ROW, OF, WINDOW, EMPTY, EXCEPT, OVER...) e FUNCTION/VIEW ficam
 * de fora: lá são nomes válidos e mudar a caixa mudaria a tabela.
 */
const MYSQL_FORMAT_KEYWORDS: ReadonlySet<string> = new Set(
  `ADD ALL ALTER ANALYZE AND AS ASC BETWEEN BINARY BY CALL CASCADE CASE CHANGE CHARACTER CHECK COLLATE COLUMN CONSTRAINT CREATE
   CROSS DATABASE DATABASES DEFAULT DELAYED DELETE DESC DESCRIBE DISTINCT DISTINCTROW DIV DROP DUAL ELSE EXISTS EXPLAIN FALSE FOR
   FORCE FOREIGN FROM FULLTEXT GRANT GROUP HAVING HIGH_PRIORITY IF IGNORE IN INDEX INNER INSERT INTERVAL INTO IS JOIN KEY KEYS KILL
   LEFT LIKE LIMIT LOCK LOW_PRIORITY NATURAL NOT NULL ON OPTIMIZE OR ORDER OUTER PRIMARY PROCEDURE RANGE READ REFERENCES REGEXP
   RENAME REPLACE RESTRICT REVOKE RIGHT RLIKE SCHEMA SELECT SET SHOW SPATIAL STRAIGHT_JOIN TABLE THEN TO TRIGGER TRUE UNION UNIQUE
   UNLOCK UNSIGNED UPDATE USE USING VALUES WHEN WHERE WITH WRITE XOR ZEROFILL`.split(/\s+/),
)

// Comentário executável do MySQL/MariaDB (aberto por "/*!", "/*!NNNNN" ou "/*M!"): o corpo é SQL executado que a verificação
// não enxerga (pode esconder um DELETE FROM ou um ponto antes de um nome). Qualquer um, em qualquer lugar do texto bruto, recusa.
const MYSQL_EXECUTED_OPENER = /\/\*M?!/

// Corpo seguro de uma dica de otimizador: sem aspas, crases, barra invertida, #, "--" nem "/" (que poderiam esconder ou
// antecipar o fim real do comentário).
const SAFE_EXECUTED_BODY = /^[\w\s,.()=<>+*%!:@$-]*$/

/** Há dica de otimizador (aberta por "/*+") cujo fim real não dá para provar? Procura no texto bruto (na dúvida, recusa). */
function hasUnprovableExecutedComment(text: string): boolean {
  const opener = /\/\*\+/g
  for (let m = opener.exec(text); m; m = opener.exec(text)) {
    const start = m.index + m[0].length
    const end = text.indexOf('*/', start)
    if (end < 0) return true
    const body = text.slice(start, end)
    if (!SAFE_EXECUTED_BODY.test(body) || body.includes('--')) return true
  }
  return false
}
const MYSQL_SAFE_POSITION: Record<string, (c: Ctx) => boolean> = {
  START: (c) => c.prev === undefined || c.prev === ';',
  COMMIT: (c) => c.prev === undefined || c.prev === ';',
  ROLLBACK: (c) => c.prev === undefined || c.prev === ';',
  BEGIN: (c) => c.prev === undefined || c.prev === ';',
  TRUNCATE: (c) => c.prev === undefined || c.prev === ';',
  TRANSACTION: (c) => c.prev === 'START',
  WORK: (c) => c.prev === 'COMMIT' || c.prev === 'ROLLBACK' || c.prev === 'BEGIN',
  OFFSET: (c) => c.prev2 === 'LIMIT' && /^\d+$/.test(c.prev ?? ''),
  DUPLICATE: (c) => c.prev === 'ON' && c.next === 'KEY',
  END: (c) => c.caseOpen,
  TEMPORARY: (c) => c.prev === 'CREATE' || c.prev === 'DROP',
  VIEW: (c) => c.prev === 'CREATE' || c.prev === 'REPLACE' || c.prev === 'ALTER' || c.prev === 'DROP',
  TABLES: (c) => c.prev === 'SHOW' || c.prev === 'LOCK' || c.prev === 'UNLOCK',
  COLUMNS: (c) => c.prev === 'SHOW' || c.prev === 'FULL',
}

// Comentários que o MySQL/MariaDB executa ou lê (abertos por "/*!", "/*!NNNNN", "/*M!" e as dicas "/*+"): nem a indentação pode mudar.
const MYSQL_EXECUTED_COMMENT = /^\/\*(?:!|M!|\+)/

const TOKEN = /--[^\n]*|\/\*[\s\S]*?\*\/|'(?:[^']|'')*'|\[[^\]]*\]|"[^"]*"|[A-Za-z_@#][\w@#$]*|\d[\w.]*|\S/g
// MySQL: comentários `#` e `-- `, strings com barra invertida (aspas simples ou duplas) e identificadores entre crases.
const MYSQL_TOKEN = /--(?=\s|$)[^\n]*|#[^\n]*|\/\*[\s\S]*?\*\/|'(?:[^'\\]|\\[\s\S]|'')*'|"(?:[^"\\]|\\[\s\S]|"")*"|`(?:[^`]|``)*`|[A-Za-z_@][\w@$]*|\d[\w.]*|\S/g

/**
 * Compara o texto original com o formatado, token a token. O formatador só pode mudar espaços, quebras de linha, a indentação
 * dentro de comentários e a caixa de palavras-chave. Textos entre aspas, identificadores, números e símbolos precisam ser idênticos.
 */
export function sameContent(original: string, formatted: string, provider: ProviderId = 'sqlserver'): boolean {
  if (provider === 'mysql') return sameContentMySql(original, formatted)
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

const isComment = (t: string) => t.startsWith('--') || t.startsWith('#') || t.startsWith('/*')

/** `sameContent` do MySQL: comentários executáveis idênticos e mudança de caixa só onde a palavra não pode ser nome. */
function sameContentMySql(original: string, formatted: string): boolean {
  const a = original.match(MYSQL_TOKEN) ?? []
  const b = formatted.match(MYSQL_TOKEN) ?? []
  if (a.length !== b.length) return false
  // Tokens com significado (sem comentários), em maiúsculas, para olhar o vizinho de cada palavra.
  const sig = a.map((t, i) => ({ t: t.toUpperCase(), i })).filter((s) => !isComment(a[s.i]))
  const pos = new Map(sig.map((s, k) => [s.i, k]))
  let caseDepth = 0
  for (let i = 0; i < a.length; i++) {
    const x = a[i]
    const y = b[i]
    const k = pos.get(i)
    const up = x.toUpperCase()
    if (k !== undefined && up === 'CASE') caseDepth++
    const endsCase = k !== undefined && up === 'END' && caseDepth > 0
    if (x === y) {
      if (endsCase) caseDepth--
      continue
    }
    if (isComment(x)) {
      if (MYSQL_EXECUTED_COMMENT.test(x) || MYSQL_EXECUTED_COMMENT.test(y)) return false
      if (!y.startsWith(x.startsWith('#') ? '#' : x.slice(0, 2)) || x.replace(/\s+/g, '') !== y.replace(/\s+/g, '')) return false
      continue
    }
    if (k === undefined || !/^[A-Za-z]/.test(x) || y !== up) return false
    const prev = sig[k - 1]?.t
    const next = sig[k + 1]?.t
    // Depois ou antes de ponto é sempre nome (banco.tabela, alias.coluna), mesmo sendo palavra reservada.
    if (prev === '.' || next === '.') return false
    if (!MYSQL_FORMAT_KEYWORDS.has(up)) {
      const rule = MYSQL_SAFE_POSITION[up]
      if (!rule || !rule({ prev, prev2: sig[k - 2]?.t, next, caseOpen: caseDepth > 0 })) return false
    }
    if (endsCase) caseDepth--
  }
  return true
}

/**
 * Formata um script SQL: palavras-chave em maiúsculas, uma cláusula por linha, indentação de 4 espaços. O dialeto vem do banco
 * da aba (`provider`, padrão SQL Server); o segundo argumento também aceita um formatador próprio (testes).
 *
 * - `GO` não é T-SQL: cada batch é formatado separadamente e as linhas `GO` ficam onde estavam. No MySQL, `GO` não é separador.
 * - Comentários e textos entre aspas são preservados (por isso não se usa o gerador do ScriptDom, que descarta comentários).
 * - Formatar nunca pode mudar o que o script faz: texto que termina dentro de uma string ou comentário aberto é recusado,
 *   e o resultado só é aceito se `sameContent` confirmar que só espaços e a caixa de palavras-chave mudaram. Qualquer dúvida
 *   cancela tudo e o texto fica como estava.
 */
export function formatSql(text: string, providerOrFormatter: ProviderId | Formatter = 'sqlserver', custom?: Formatter): FormatResult {
  const provider: ProviderId = typeof providerOrFormatter === 'string' ? providerOrFormatter : 'sqlserver'
  const formatter = typeof providerOrFormatter === 'function' ? providerOrFormatter : custom ?? FORMATTERS[provider]
  const mysql = provider === 'mysql'

  if (text.trim() === '') return { ok: true, text }
  // A quebra de linha extra encerra um comentário de linha que termina o texto (isso é válido); string ou comentário de bloco abertos continuam dentro.
  if (scan(text + '\n', undefined, provider).inside) return { ok: false, reason: 'há uma string ou um comentário sem fechar' }
  if (mysql && MYSQL_EXECUTED_OPENER.test(text)) return { ok: false, reason: 'há um comentário executável (/*! */) e o SQL dentro dele não é verificado' }
  if (mysql && hasUnprovableExecutedComment(text)) return { ok: false, reason: 'há uma dica de otimizador (/*+ */) com texto que não dá para analisar' }

  const lines = text.split(/\r?\n/)
  // Linhas "GO" são procuradas no texto sem comentários e strings (um GO dentro de /* */ ou de uma string não é separador).
  const masked = scan(lines.join('\n'), undefined, provider).masked.split('\n')

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
    if (!sameContent(code, formatted, provider)) return { ok: false, reason: 'o resultado mudaria o conteúdo do script' }
    out.push(formatted)
    return null
  }

  for (let i = 0; i < lines.length; i++) {
    if (!mysql && /^\s*GO(\s+\d+)?\s*$/i.test(masked[i])) {
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
