import type { DocRange } from './contracts'
import { modelPath } from './components/EditorPane'
import { formatSql } from './formatSql'
import { monaco } from './monacoSetup'

/** O que o backend precisa para decidir o que executar: texto completo, cursor e seleção (offsets no texto). */
export interface EditorSnapshot {
  text: string
  cursor: number
  selectionStart: number
  selectionEnd: number
}

const modelOf = (tabId: string) => monaco.editor.getModel(monaco.Uri.parse(modelPath(tabId)))

/** Há uma única instância do Monaco; ela só mostra o modelo da aba ativa. */
function editorShowing(tabId: string) {
  const model = modelOf(tabId)
  const editor = monaco.editor.getEditors().find((e) => e.getModel() === model)
  return model && editor ? { model, editor } : null
}

export function snapshotOf(tabId: string): EditorSnapshot | null {
  const shown = editorShowing(tabId)
  if (!shown) return null
  const { model, editor } = shown
  const pos = editor.getPosition()
  const sel = editor.getSelection()
  const cursor = pos ? model.getOffsetAt(pos) : 0
  return {
    text: model.getValue(),
    cursor,
    selectionStart: sel ? model.getOffsetAt(sel.getStartPosition()) : cursor,
    selectionEnd: sel ? model.getOffsetAt(sel.getEndPosition()) : cursor,
  }
}

/** Destaca um trecho do documento por ~600 ms (o statement executado, ou o que gerou um resultado). */
export function highlightRange(tabId: string, range: DocRange, ms = 600) {
  const shown = editorShowing(tabId)
  if (!shown) return
  const { model, editor } = shown
  const start = model.getPositionAt(range.start)
  const end = model.getPositionAt(range.start + range.length)
  const r = new monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column)
  editor.revealRangeInCenterIfOutsideViewport(r)
  const ids = model.deltaDecorations([], [{ range: r, options: { className: 'exec-highlight' } }])
  window.setTimeout(() => {
    if (!model.isDisposed()) model.deltaDecorations(ids, [])
  }, ms)
}

/** Leva o cursor ao início da linha (base 1) e devolve o foco ao editor. */
export function revealLine(tabId: string, line: number) {
  const shown = editorShowing(tabId)
  if (!shown) return
  const { model, editor } = shown
  const lineNumber = Math.min(Math.max(1, line), model.getLineCount())
  editor.setPosition({ lineNumber, column: 1 })
  editor.revealLineInCenter(lineNumber)
  editor.focus()
}

/**
 * "Envolver em transação": insere `BEGIN TRAN;` antes do trecho e, depois dele, as linhas comentadas
 * `-- COMMIT;` e `-- ROLLBACK;`. Não executa nada: o usuário revisa e roda quando quiser.
 */
export function wrapInTransaction(tabId: string, range: DocRange) {
  const shown = editorShowing(tabId)
  if (!shown) return
  const { model, editor } = shown
  const start = model.getPositionAt(range.start)
  const end = model.getPositionAt(range.start + range.length)
  const at = (p: { lineNumber: number; column: number }) => new monaco.Range(p.lineNumber, p.column, p.lineNumber, p.column)
  editor.executeEdits('sqldesk-wrap-transaction', [
    { range: at(end), text: '\n-- COMMIT;\n-- ROLLBACK;' },
    { range: at(start), text: 'BEGIN TRAN;\n' },
  ])
  editor.focus()
}

export type FormatOutcome = { status: 'formatted' | 'unchanged' } | { status: 'refused'; reason: string }

/**
 * Formata a seleção ou, sem seleção, o texto inteiro da aba. A edição entra na pilha de desfazer (Ctrl+Z volta ao texto anterior)
 * e, se o formatador não puder garantir que o conteúdo é o mesmo, o texto não é tocado.
 */
export function formatEditor(tabId: string): FormatOutcome | null {
  const shown = editorShowing(tabId)
  if (!shown) return null
  const { model, editor } = shown
  const selection = editor.getSelection()
  const range = selection && !selection.isEmpty() ? selection : model.getFullModelRange()
  const original = model.getValueInRange(range)

  const result = formatSql(original)
  if (!result.ok) return { status: 'refused', reason: result.reason }
  // O modelo pode usar CRLF e o formatador devolve LF: a diferença só de fim de linha não conta como mudança.
  if (result.text.replace(/\r\n/g, '\n') === original.replace(/\r\n/g, '\n')) return { status: 'unchanged' }

  editor.pushUndoStop()
  editor.executeEdits('sqldesk-format', [{ range, text: result.text }])
  editor.pushUndoStop()
  editor.focus()
  return { status: 'formatted' }
}
