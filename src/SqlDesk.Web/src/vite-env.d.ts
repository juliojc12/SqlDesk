/// <reference types="vite/client" />

// O entry "edcore.main" (editor sem todas as linguagens) não traz tipos próprios: reaproveita os do pacote.
declare module 'monaco-editor/esm/vs/editor/edcore.main' {
  export * from 'monaco-editor'
}

declare module 'monaco-editor/esm/vs/basic-languages/sql/sql' {
  export const language: { tokenizer: Record<string, unknown> }
}
