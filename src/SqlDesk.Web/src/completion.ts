import { getAutoAlias } from './settings'
import { connectionOfTab, getMeta } from './metadataStore'
import { monaco } from './monacoSetup'
import { suggest, type Suggestion, type SuggestionKind } from './suggest'

const K = monaco.languages.CompletionItemKind

const KIND: Record<SuggestionKind, monaco.languages.CompletionItemKind> = {
  table: K.Struct,
  view: K.Interface,
  column: K.Field,
  schema: K.Module,
  procedure: K.Function,
  function: K.Function,
  keyword: K.Keyword,
  snippet: K.Snippet,
}

/** O modelo de cada aba tem o caminho `inmemory://sqldesk/<tabId>.sql`. */
const tabIdOf = (uri: monaco.Uri) => uri.path.replace(/^\//, '').replace(/\.sql$/, '')

function toItem(s: Suggestion, range: monaco.IRange): monaco.languages.CompletionItem {
  return {
    label: s.description ? { label: s.label, description: s.description } : s.label,
    kind: KIND[s.kind],
    insertText: s.insertText,
    insertTextRules: s.isSnippet ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
    detail: s.detail,
    filterText: s.filterText,
    sortText: s.sortText,
    range,
    command: s.retrigger ? { id: 'editor.action.triggerSuggest', title: '' } : undefined,
  }
}

let registered = false

/** Registra o autocomplete contextual do SQL (uma vez). O cache vem de `metadataStore`, por conexão da aba. */
export function registerCompletion() {
  if (registered) return
  registered = true
  monaco.languages.registerCompletionItemProvider('sql', {
    triggerCharacters: ['.', ' '],
    provideCompletionItems(model, position, context) {
      const index = getMeta(connectionOfTab(tabIdOf(model.uri))).index
      const result = suggest(model.getValue(), model.getOffsetAt(position), index, { autoAlias: getAutoAlias() })

      // O espaço só abre a lista onde ela é esperada (depois de FROM, JOIN, EXEC...); no resto seria ruído.
      const bySpace = context.triggerKind === monaco.languages.CompletionTriggerKind.TriggerCharacter && context.triggerCharacter === ' '
      if (bySpace && result.context.kind !== 'table' && result.context.kind !== 'exec') return { suggestions: [] }

      const word = model.getWordUntilPosition(position)
      const range = { startLineNumber: position.lineNumber, endLineNumber: position.lineNumber, startColumn: word.startColumn, endColumn: word.endColumn }
      return { suggestions: result.items.map((s) => toItem(s, range)) }
    },
  })
}
