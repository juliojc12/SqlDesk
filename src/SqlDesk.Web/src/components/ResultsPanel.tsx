import { useCallback, useEffect, useRef, useState } from 'react'
import { identityView, type GridView } from '../exporter'
import { getCsvDelimiter } from '../settings'
import { MESSAGES_TAB, type ResultSet, type TabResults } from '../results'
import { DataGrid } from './DataGrid'
import { RunningBar, RunningIndicator } from './RunningIndicator'

interface Props {
  results: TabResults
  color: string
  onActivate: (key: string) => void
  onJumpToLine: (line: number) => void
  onLoadAll: (set: ResultSet) => void
  /** "Carregar todas" reexecuta o trecho; só faz sentido com a aba conectada e livre. */
  canLoadAll: boolean
  /** Exporta o resultado ativo; a grade informa a ordem de colunas e a ordenação atuais. */
  onExport: (format: 'csv' | 'xlsx', set: ResultSet, view: GridView) => void
  exportBusy: boolean
  onStop: () => void
}

function loadCopyWithHeader(): boolean {
  try {
    return localStorage.getItem('copyWithHeader') === '1'
  } catch {
    return false
  }
}

function Messages({ results, onJumpToLine }: Pick<Props, 'results' | 'onJumpToLine'>) {
  const end = useRef<HTMLDivElement>(null)
  useEffect(() => {
    end.current?.scrollIntoView({ block: 'end' })
  }, [results.messages.length])

  if (results.messages.length === 0) {
    return <div className="flex h-full items-center justify-center text-sm text-muted">Nenhuma mensagem.</div>
  }
  return (
    <div className="thin-scroll h-full select-text overflow-auto px-5 py-3 font-mono text-[13px]" role="log" aria-label="Mensagens">
      {results.messages.map((m) => (
        <p key={m.id} className={`flex gap-3 py-0.5 ${m.kind === 'error' ? 'text-danger' : m.kind === 'info' ? 'text-fg' : 'text-muted'}`}>
          <span className="whitespace-pre-wrap break-words">{m.text}</span>
          {m.line !== undefined && (
            <button className="shrink-0 underline decoration-dotted hover:decoration-solid" onClick={() => onJumpToLine(m.line as number)}>
              linha {m.line}
            </button>
          )}
        </p>
      ))}
      <div ref={end} />
    </div>
  )
}

export function ResultsPanel({ results, color, onActivate, onJumpToLine, onLoadAll, canLoadAll, onExport, exportBusy, onStop }: Props) {
  const [copyWithHeader, setCopyWithHeader] = useState(loadCopyWithHeader)
  // A grade informa a visão (colunas e ordenação) por um ref: mudar de coluna ou ordenar não deve renderizar o painel inteiro.
  const view = useRef<GridView | null>(null)
  const onViewChange = useCallback((v: GridView) => {
    view.current = v
  }, [])
  const active = results.sets.find((s) => s.key === results.active) ?? null
  const showMessages = results.active === MESSAGES_TAB || !active
  const canExport = !showMessages && !!active && active.done && !exportBusy && !results.running
  const hasError = results.messages.some((m) => m.kind === 'error')
  // Rodando e ainda sem nada desta execução para mostrar: em vez de resultado antigo ou "Nenhuma mensagem", o indicador de carregamento.
  const producedYet = results.sets.some((s) => s.key.startsWith(`${results.executionId}:`))
  const waiting = results.running && !producedYet && !(results.keepPrevious && results.sets.length > 0)

  const tabClass = (on: boolean) =>
    `-mb-px flex items-center gap-2 border-b-2 px-1 py-2 text-[15px] ${on ? 'text-fg' : 'border-transparent text-muted hover:text-fg'}`
  // Exportar: com borda e fundo, para parecer botão (e não texto solto ao lado das abas).
  const exportBtn = 'flex h-8 items-center gap-2 rounded-md border border-line bg-hover px-3 text-sm font-medium text-fg enabled:hover:border-muted enabled:hover:bg-selected disabled:opacity-40'

  return (
    <section className="flex h-full min-h-0 flex-col bg-surface" aria-label="Resultados">
      <div className="flex h-11 shrink-0 items-center gap-5 border-b border-line px-5">
        <div role="tablist" className="thin-scroll flex min-w-0 gap-5 overflow-x-auto overflow-y-hidden">
          {results.sets.map((s) => {
            const on = !showMessages && s.key === active?.key
            return (
              <button
                key={s.key}
                role="tab"
                aria-selected={on}
                style={on ? { borderBottomColor: color } : undefined}
                className={`${tabClass(on)} shrink-0 whitespace-nowrap`}
                onClick={() => onActivate(s.key)}
              >
                {s.title}
                <span className="text-[12px] text-muted">{s.rowCount.toLocaleString('pt-BR')}</span>
              </button>
            )
          })}
          <button
            role="tab"
            aria-selected={showMessages}
            style={showMessages ? { borderBottomColor: color } : undefined}
            className={`${tabClass(showMessages)} shrink-0`}
            onClick={() => onActivate(MESSAGES_TAB)}
          >
            Mensagens
            {hasError && <span className="h-2 w-2 rounded-full bg-danger" aria-label="há erros" />}
          </button>
        </div>
        <div className="ml-auto flex shrink-0 gap-2">
          <button disabled={!canExport} className={exportBtn} title={`Exportar o resultado ativo em CSV (separador ${getCsvDelimiter()}, em Configurações)`} onClick={() => active && onExport('csv', active, view.current ?? identityView(active.columns.length))}>
            <span className="icon">&#xE896;</span> CSV
          </button>
          <button disabled={!canExport} className={exportBtn} title="Exportar o resultado ativo em XLSX" onClick={() => active && onExport('xlsx', active, view.current ?? identityView(active.columns.length))}>
            <span className="icon">&#xE896;</span> XLSX
          </button>
        </div>
      </div>

      {results.running && <RunningBar color={color} />}
      <div className="min-h-0 flex-1">
        {waiting ? (
          <RunningIndicator since={results.startedAt} color={color} onStop={onStop} />
        ) : showMessages ? (
          <Messages results={results} onJumpToLine={onJumpToLine} />
        ) : (
          <DataGrid
            key={active.key}
            set={active}
            color={color}
            copyWithHeader={copyWithHeader}
            onCopyWithHeaderChange={(v) => {
              setCopyWithHeader(v)
              try {
                localStorage.setItem('copyWithHeader', v ? '1' : '0')
              } catch {
                /* preferência de conveniência */
              }
            }}
            canLoadAll={canLoadAll}
            onViewChange={onViewChange}
            onLoadAll={() => onLoadAll(active)}
          />
        )}
      </div>
    </section>
  )
}
