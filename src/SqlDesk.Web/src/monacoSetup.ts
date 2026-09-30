// Monaco empacotado localmente (nada de CDN: o app precisa funcionar offline).
import { loader } from '@monaco-editor/react'
import * as monaco from 'monaco-editor/esm/vs/editor/edcore.main'
import 'monaco-editor/esm/vs/basic-languages/sql/sql.contribution'
import EditorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker'

self.MonacoEnvironment = { getWorker: () => new EditorWorker() }
loader.config({ monaco })

// Cores do mockup: fundo do editor igual ao das superfícies, palavras-chave em azul.
monaco.editor.defineTheme('sqldesk-dark', {
  base: 'vs-dark',
  inherit: true,
  rules: [
    { token: 'keyword.sql', foreground: '569CD6' },
    { token: 'operator.sql', foreground: 'D4D4D4' },
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

monaco.editor.defineTheme('sqldesk-light', {
  base: 'vs',
  inherit: true,
  rules: [
    { token: 'keyword.sql', foreground: '0451A5' },
    { token: 'string.sql', foreground: 'A31515' },
    { token: 'number.sql', foreground: '098658' },
    { token: 'comment.sql', foreground: '008000' },
    { token: 'predefined.sql', foreground: '795E26' },
  ],
  colors: {
    'editor.background': '#FFFFFF',
    'editor.lineHighlightBackground': '#F5F5F5',
  },
})

export { monaco }
