import { NEUTRAL_COLOR, tintedSurface } from '../colors'
import type { ConnectionInfo } from '../contracts'
import { providerOf } from '../providers'
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
  needs_confirmation: '',
  advise_transaction: '',
  pending_decision: 'aguardando commit ou rollback',
  tran_lost: 'transação encerrada pelo script',
}

/** Barra de status tingida com a cor da conexão da aba ativa; o texto mantém contraste >= 4,5:1. */
interface Props {
  tab: Tab | null
  connection: ConnectionInfo | null
  results: TabResults
  /** Quantas abas têm transação aberta, e se a aba ativa é uma delas. */
  openTranTabs: number
  activeTran: boolean
  tranBusy: boolean
  onCommit: () => void
  onRollback: () => void
}

export function StatusBar({ tab, connection, results, openTranTabs, activeTran, tranBusy, onCommit, onRollback }: Props) {
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
          {connection && (
            <span className="whitespace-nowrap">
              {providerOf(connection.settings).name}
              {tab.serverVersion ? ` ${tab.serverVersion}` : ` · ${connection.settings.server}${connection.settings.database ? ` / ${connection.settings.database}` : ''}`}
            </span>
          )}
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
      {openTranTabs > 0 && (
        <span className="ml-auto flex shrink-0 items-center gap-2 whitespace-nowrap">
          <span className="rounded bg-amber-500 px-1.5 text-xs font-bold leading-5 text-black">TRAN</span>
          {openTranTabs} {openTranTabs === 1 ? 'aba com transação aberta' : 'abas com transação aberta'}
          {activeTran && (
            <>
              <button disabled={tranBusy} onClick={onCommit} className="rounded border border-current/40 px-2 text-sm font-medium hover:bg-black/10 disabled:opacity-50">Commit</button>
              <button disabled={tranBusy} onClick={onRollback} className="rounded border border-current/40 px-2 text-sm font-medium hover:bg-black/10 disabled:opacity-50">Rollback</button>
            </>
          )}
        </span>
      )}
    </footer>
  )
}
