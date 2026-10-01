import { NEUTRAL_COLOR, tintedSurface } from '../colors'
import type { ConnectionInfo } from '../contracts'
import { formatElapsed, type TabResults } from '../results'
import type { Tab } from '../tabsState'

const LABEL: Record<Tab['status'], string> = {
  idle: 'desconectado',
  disconnected: 'desconectado',
  connecting: 'conectando…',
  connected: 'conectado',
  error: 'falha na conexão',
  'needs-password': 'senha necessária',
  'no-connection': 'sem conexão',
}

const RUN_LABEL: Record<NonNullable<TabResults['lastRun']>['status'], string> = {
  completed: 'concluído',
  error: 'com erro',
  cancelled: 'cancelado',
  refused: 'bloqueado',
  nothing: '',
}

/** Barra de status tingida com a cor da conexão da aba ativa; o texto mantém contraste >= 4,5:1. */
export function StatusBar({ tab, connection, results }: { tab: Tab | null; connection: ConnectionInfo | null; results: TabResults }) {
  const color = connection?.color ?? NEUTRAL_COLOR
  const { bg, fg } = tintedSurface(color)
  const run = results.lastRun
  return (
    <footer style={{ backgroundColor: bg, color: fg }} className="flex h-10 shrink-0 items-center gap-6 px-5 text-[15px]" role="status">
      {tab ? (
        <>
          <span className="flex shrink-0 items-center gap-2 whitespace-nowrap">
            <span className="h-2.5 w-2.5 rounded-full" style={{ backgroundColor: color }} aria-hidden />
            {connection?.name ?? 'Sem conexão'} · {LABEL[tab.status]}
          </span>
          {tab.serverVersion && <span className="whitespace-nowrap">SQL Server {tab.serverVersion}</span>}
          {results.running ? (
            <span className="whitespace-nowrap font-medium">Executando…</span>
          ) : (
            run && (
              <span className="whitespace-nowrap">
                {run.totalRows.toLocaleString('pt-BR')} {run.totalRows === 1 ? 'linha' : 'linhas'} · {formatElapsed(run.elapsedMs)}
                {RUN_LABEL[run.status] && ` · ${RUN_LABEL[run.status]}`}
              </span>
            )
          )}
        </>
      ) : (
        <span>Pronto</span>
      )}
    </footer>
  )
}
