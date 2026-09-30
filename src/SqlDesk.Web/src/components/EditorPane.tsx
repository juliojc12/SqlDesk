import Editor from '@monaco-editor/react'
import '../monacoSetup'
import type { Theme } from '../useTheme'

export const modelPath = (tabId: string) => `inmemory://sqldesk/${tabId}.sql`

interface Props {
  tabId: string
  initialText: string
  theme: Theme
  onChange: (text: string) => void
}

/**
 * Uma única instância do Monaco; cada aba tem o seu modelo (mantém desfazer e posição do cursor ao alternar).
 * O texto é a verdade do modelo; o React só o acompanha via onChange.
 */
export function EditorPane({ tabId, initialText, theme, onChange }: Props) {
  return (
    <Editor
      height="100%"
      path={modelPath(tabId)}
      defaultLanguage="sql"
      defaultValue={initialText}
      theme={theme === 'dark' ? 'sqldesk-dark' : 'sqldesk-light'}
      keepCurrentModel
      onChange={(v) => onChange(v ?? '')}
      loading={<div className="p-4 text-sm text-muted">Carregando editor…</div>}
      options={{
        fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, monospace',
        fontSize: 15,
        lineHeight: 26,
        minimap: { enabled: false },
        automaticLayout: true,
        scrollBeyondLastLine: false,
        padding: { top: 12, bottom: 12 },
        renderLineHighlight: 'line',
        tabSize: 4,
        insertSpaces: true,
        wordWrap: 'off',
        smoothScrolling: true,
        contextmenu: false,
        overviewRulerLanes: 0,
        scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10 },
      }}
    />
  )
}
