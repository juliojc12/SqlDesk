import { generateAlias, RESERVED_WORDS } from './alias'
import type { MetaIndex, MetaObject } from './metadataIndex'
import { aliasReservedWords, MYSQL_RESERVED_WORDS, PROVIDERS, type ProviderId } from './providers'
import { analyze, type Context, type TableKeyword, type TableRef } from './sqlContext'
import { FUNCTIONS, KEYWORDS, SNIPPETS } from './sqlKeywords'

export type SuggestionKind = 'table' | 'view' | 'column' | 'schema' | 'procedure' | 'function' | 'keyword' | 'snippet'

export interface Suggestion {
  label: string
  kind: SuggestionKind
  insertText: string
  /** Texto à direita do rótulo: o tipo da coluna, o tipo do objeto. */
  description?: string
  detail?: string
  filterText?: string
  sortText: string
  isSnippet?: boolean
  /** Reabre a lista depois de inserir (schema: o usuário segue com "schema." e escolhe o objeto). */
  retrigger?: boolean
}

export interface SuggestOptions {
  autoAlias: boolean
  /** Banco da conexão da aba (crases e palavras reservadas do MySQL); ausente vale SQL Server. */
  provider?: ProviderId
}

const eq = (a: string | undefined, b: string | undefined) => a !== undefined && b !== undefined && a.toLowerCase() === b.toLowerCase()

/**
 * Nome seguro para usar em SQL: entre colchetes (SQL Server) ou crases (MySQL) se tiver caracteres especiais ou for
 * palavra reservada daquele banco.
 */
export function quoteIfNeeded(name: string, provider: ProviderId = 'sqlserver'): string {
  if (provider === 'mysql')
    return /^[A-Za-z_][\w$]*$/.test(name) && !MYSQL_RESERVED_WORDS.has(name.toUpperCase()) ? name : PROVIDERS.mysql.quote(name)
  return /^[A-Za-z_][\w@#$]*$/.test(name) && !RESERVED_WORDS.has(name.toUpperCase()) ? name : PROVIDERS.sqlserver.quote(name)
}

/**
 * `Clientes` para o schema padrão; `vendas.Pedidos` para os demais. O padrão é `dbo`, ou, com o índice de um
 * banco sem nível de schema (MySQL), o banco atual da conexão.
 */
export function displayName(o: MetaObject, index?: MetaIndex, provider: ProviderId = 'sqlserver'): string {
  const q = (n: string) => quoteIfNeeded(n, provider)
  return (index ? index.isDefaultSchema(o.schema) : eq(o.schema, 'dbo')) ? q(o.name) : `${q(o.schema)}.${q(o.name)}`
}

/** Há um alias digitado logo depois do nome (`FROM Clientes| c`)? Então não se gera outro. */
function aliasAlreadyTyped(after: string, provider: ProviderId): boolean {
  const m = /^[\w@#$]*[ \t]+(?:AS[ \t]+)?([A-Za-z_@#][\w@#$]*|`(?:[^`]|``)*`)/i.exec(after)
  return !!m && !aliasReservedWords(provider).has(m[1].toUpperCase())
}

function keywordItems(group = '3'): Suggestion[] {
  return KEYWORDS.map((k) => ({ label: k, kind: 'keyword' as const, insertText: k, sortText: `${group}${k}` }))
}

function functionItems(group = '2'): Suggestion[] {
  return FUNCTIONS.map((f) => ({
    label: f, kind: 'function' as const, insertText: `${f}($0)`, isSnippet: true, description: 'função', sortText: `${group}${f}`,
  }))
}

function snippetItems(): Suggestion[] {
  return SNIPPETS.map((s) => ({
    label: s.trigger, kind: 'snippet' as const, insertText: s.body, isSnippet: true, description: s.description, sortText: `4${s.trigger}`,
  }))
}

function columnSuggestions(o: MetaObject, index: MetaIndex, p: ProviderId): Suggestion[] {
  return index.columnsOf(o).map((c, i) => ({
    label: c.name,
    kind: 'column' as const,
    insertText: quoteIfNeeded(c.name, p),
    description: `${c.type}${c.nullable ? '' : ' not null'}`,
    detail: `${o.schema}.${o.name}`,
    sortText: `1${String(i).padStart(5, '0')}`,
  }))
}

function resolve(ref: TableRef, index: MetaIndex): MetaObject | undefined {
  return index.find(ref.schema, ref.name)
}

function qualifiedItems(parts: string[], refs: TableRef[], index: MetaIndex, p: ProviderId): Suggestion[] {
  if (parts.length === 2) {
    const o = index.find(parts[0], parts[1])
    return o ? columnSuggestions(o, index, p) : []
  }
  if (parts.length !== 1) return []
  const q = parts[0]

  const byAlias = refs.find((r) => eq(r.alias, q))
  if (byAlias) {
    const o = resolve(byAlias, index)
    return o ? columnSuggestions(o, index, p) : []
  }
  if (index.hasSchema(q)) {
    return index
      .objectsOfSchema(q)
      .filter((o) => o.type !== 'procedure')
      .map((o) => ({
        label: o.name,
        kind: o.type === 'view' ? ('view' as const) : o.type === 'function' ? ('function' as const) : ('table' as const),
        insertText: quoteIfNeeded(o.name, p),
        description: o.type === 'table' ? 'tabela' : o.type === 'view' ? 'view' : 'função',
        sortText: `1${o.name}`,
      }))
  }
  const o = index.find(undefined, q)
  return o ? columnSuggestions(o, index, p) : []
}

function columnContextItems(refs: TableRef[], index: MetaIndex, p: ProviderId): Suggestion[] {
  const resolved = refs.flatMap((r) => {
    const o = resolve(r, index)
    return o ? [{ ref: r, obj: o }] : []
  })
  // Coluna presente em mais de uma tabela do statement: leva o alias (ou o nome da tabela) como prefixo.
  const counts = new Map<string, number>()
  for (const { obj } of resolved)
    for (const c of index.columnsOf(obj)) counts.set(c.name.toLowerCase(), (counts.get(c.name.toLowerCase()) ?? 0) + 1)

  const items: Suggestion[] = []
  for (const { ref, obj } of resolved) {
    const qualifier = quoteIfNeeded(ref.alias ?? ref.name, p)
    for (const s of columnSuggestions(obj, index, p)) {
      if ((counts.get(s.label.toLowerCase()) ?? 0) > 1) {
        items.push({ ...s, label: `${qualifier}.${s.label}`, insertText: `${qualifier}.${s.insertText}`, filterText: `${s.label} ${qualifier}.${s.label}` })
      } else items.push(s)
    }
  }
  return items
}

function tableContextItems(keyword: TableKeyword, refs: TableRef[], after: string, index: MetaIndex, opts: SuggestOptions): Suggestion[] {
  const p = opts.provider ?? 'sqlserver'
  const wantAlias = opts.autoAlias && (keyword === 'FROM' || keyword === 'JOIN') && !aliasAlreadyTyped(after, p)
  const used = refs.flatMap((r) => [r.alias, r.alias ? undefined : r.name]).filter((x): x is string => !!x)

  const items: Suggestion[] = index.objects
    .filter((o) => o.type === 'table' || o.type === 'view')
    .map((o) => {
      const name = displayName(o, index, p)
      return {
        label: index.isDefaultSchema(o.schema) ? o.name : `${o.schema}.${o.name}`,
        kind: o.type,
        insertText: wantAlias ? `${name} ${generateAlias(o.name, used, aliasReservedWords(p))}` : name,
        description: o.type === 'table' ? 'tabela' : 'view',
        filterText: `${o.name} ${o.schema}.${o.name}`,
        sortText: `1${index.isDefaultSchema(o.schema) ? '0' : '1'}${o.name}`,
      }
    })

  for (const s of index.schemas.filter((s) => !index.isDefaultSchema(s))) {
    items.push({ label: s, kind: 'schema', insertText: `${quoteIfNeeded(s, p)}.`, description: index.hasSchemaLevel ? 'schema' : 'banco', sortText: `2${s}`, retrigger: true })
  }
  return items
}

export interface SuggestResult {
  context: Context
  items: Suggestion[]
}

/** Sugestões para o cursor em `offset`, com o contexto determinado pelos tokens antes dele no statement atual. */
export function suggest(text: string, offset: number, index: MetaIndex | null, opts: SuggestOptions): SuggestResult {
  const p = opts.provider ?? 'sqlserver'
  const a = analyze(text, offset, p)
  const { context } = a
  if (a.inside) return { context, items: [] }

  switch (context.kind) {
    case 'qualified':
      return { context, items: index ? qualifiedItems(context.parts, a.refs, index, p) : [] }
    case 'table':
      return { context, items: index ? tableContextItems(context.keyword, a.refs, a.after, index, opts) : [] }
    case 'exec': {
      const procs = (index?.objects ?? [])
        .filter((o) => o.type === 'procedure')
        .map((o) => ({
          label: (index?.isDefaultSchema(o.schema) ?? eq(o.schema, 'dbo')) ? o.name : `${o.schema}.${o.name}`, kind: 'procedure' as const, insertText: displayName(o, index ?? undefined, p),
          description: 'procedure', filterText: `${o.name} ${o.schema}.${o.name}`, sortText: `1${o.name}`,
        }))
      return { context, items: procs }
    }
    case 'columns':
      return { context, items: [...(index ? columnContextItems(a.refs, index, p) : []), ...functionItems(), ...keywordItems()] }
    default:
      return { context, items: [...keywordItems('1'), ...snippetItems(), ...functionItems('3')] }
  }
}
