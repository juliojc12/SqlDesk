// Monaco empacotado localmente (nada de CDN: o app precisa funcionar offline).
import { loader } from '@monaco-editor/react'
import * as monaco from 'monaco-editor/esm/vs/editor/edcore.main'
import 'monaco-editor/esm/vs/basic-languages/sql/sql.contribution'
import { language as sqlLanguage } from 'monaco-editor/esm/vs/basic-languages/sql/sql'
import EditorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker'

self.MonacoEnvironment = { getWorker: () => new EditorWorker() }
loader.config({ monaco })

// O Monaco classifica AND, OR, JOIN, IN, LIKE... como "operator", o mesmo token de = e +,
// e por isso essas palavras ficavam sem cor. Aqui elas viram "keyword" (a busca já ignora
// maiúsculas/minúsculas) e só os símbolos continuam como operador.
type Rule = unknown[] | { include: string }
const root = sqlLanguage.tokenizer.root as Rule[]
const sqlTokens = {
  ...sqlLanguage,
  tokenizer: {
    ...sqlLanguage.tokenizer,
    root: root.map((rule) => {
      if (!Array.isArray(rule)) return rule
      const action = rule[1] as { cases?: Record<string, string> } | string
      if (typeof action === 'object' && action.cases?.['@operators']) {
        return [rule[0], { cases: { ...action.cases, '@operators': 'keyword' } }]
      }
      if (action === 'operator') return [rule[0], 'operator.symbol']
      return rule
    }),
  },
}
monaco.languages.setMonarchTokensProvider('sql', sqlTokens as never)

// Cores do mockup: fundo do editor igual ao das superfícies, palavras-chave em azul.
monaco.editor.defineTheme('sqldesk-dark', {
  base: 'vs-dark',
  inherit: true,
  rules: [
    { token: 'keyword.sql', foreground: '569CD6' },
    { token: 'operator.symbol.sql', foreground: 'D4D4D4' },
    { token: 'string.sql', foreground: 'CE9178' },
    { token: 'number.sql', foreground: 'B5CEA8' },
    { token: 'comment.sql', foreground: '6A9955' },
    { token: 'comment.quote.sql', foreground: '6A9955' },
    { token: 'predefined.sql', foreground: 'DCDCAA' },
  ],
  colors: {
    'editor.background': '#1C1C1C',
    'editor.lineHighlightBackground': '#242424',
    'editorLineNumber.foreground': '#6E6E6E',
    'editorLineNumber.activeForeground': '#B0B0B0',
    'editorGutter.background': '#1C1C1C',
    'editor.selectionBackground': '#264F78',
    'editorWidget.background': '#1F1F1F',
    'editorSuggestWidget.background': '#1F1F1F',
    'editorSuggestWidget.border': '#3A3A3A',
    'editorSuggestWidget.selectedBackground': '#002347',
  },
})

export { monaco }
