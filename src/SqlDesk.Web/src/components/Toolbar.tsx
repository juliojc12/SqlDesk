import { textOn } from '../colors'
import type { ConnectionInfo } from '../contracts'
import type { Tab } from '../tabsState'

interface Props {
  tab: Tab
  connection: ConnectionInfo | null
  color: string
  onSave: () => void
  onOpen: () => void
  onConnect: () => void
  onDisconnect: () => void
  onPickConnection: () => void
  running: boolean
  onRun: () => void
  onRunScript: () => void
  onStop: () => void
  tranCount: number
  onBeginTran: () => void
  autoAlias: boolean
  onToggleAlias: () => void
  metaLoading: boolean
  onFormat: () => void
  onRefreshMetadata: () => void
}

const ghost = 'flex h-8 shrink-0 items-center gap-2 whitespace-nowrap rounded-md px-3 text-sm text-fg enabled:hover:bg-hover disabled:opacity-40'

export function Toolbar({ tab, connection, color, onSave, onOpen, onConnect, onDisconnect, onPickConnection, running, onRun, onRunScript, onStop, tranCount, onBeginTran, autoAlias, onToggleAlias, metaLoading, onRefreshMetadata, onFormat }: Props) {
  const connected = tab.status === 'connected'
  const canRun = connected && !running
  return (
    <div className="flex h-14 shrink-0 items-center gap-2 border-b border-line bg-surface px-4">
      <button
        disabled={!canRun}
        onClick={onRun}
        title="Executar a seleção ou o statement sob o cursor (Ctrl+Enter). Ctrl+\ abre o resultado em nova sub-aba"
        style={{ backgroundColor: color, color: textOn(color) }}
        className="flex h-9 items-center gap-2 rounded-lg px-4 text-[15px] font-medium disabled:opacity-50"
      >
        <span className="icon">&#xE768;</span> Executar
      </button>
      <button disabled={!canRun} onClick={onRunScript} title="Executar o script inteiro (F5)" className={ghost}>
        <span className="icon">&#xEA37;</span> Script
      </button>
      <button
        disabled={!running}
        onClick={onStop}
        title="Parar (Esc)"
        aria-label="Parar"
        style={running ? { backgroundColor: '#C42B1C', color: '#FFFFFF' } : undefined}
        className={`${running ? 'font-medium' : 'text-fg opacity-40'} flex h-8 items-center gap-2 rounded-md px-3 text-sm`}
      >
        <span className="icon">&#xE71A;</span> {running && 'Parar'}
      </button>
      <button
        disabled={!canRun || tranCount > 0}
        onClick={onBeginTran}
        title={tranCount > 0 ? 'Há uma transação aberta nesta aba (Commit e Rollback na barra de status)' : 'Iniciar uma transação nesta aba (BEGIN TRANSACTION)'}
        className={ghost}
      >
        <span className="icon">&#xE8C8;</span> {tranCount > 0 ? 'Transação aberta' : 'Transação'}
      </button>
      <span className="mx-1 h-5 w-px bg-line" aria-hidden />
      <button className={`${ghost} icon w-9 justify-center px-0`} title="Salvar (Ctrl+S)" aria-label="Salvar" onClick={onSave}>&#xE74E;</button>
      <button className={`${ghost} icon w-9 justify-center px-0`} title="Abrir arquivo .sql (Ctrl+O)" aria-label="Abrir arquivo" onClick={onOpen}>&#xE8E5;</button>
      <span className="mx-1 h-5 w-px bg-line" aria-hidden />
      <button
        className={`${ghost} icon w-9 justify-center px-0 ${metaLoading ? 'animate-pulse' : ''}`}
        title="Atualizar metadados (tabelas e colunas do autocomplete)"
        aria-label="Atualizar metadados"
        disabled={tab.connectionId === null}
        onClick={onRefreshMetadata}
      >
        &#xE72C;
      </button>
      <button className={ghost} title="Formatar o SQL: a seleção ou o texto todo (Shift+Alt+F)" onClick={onFormat}>
        Formatar
      </button>
      <button
        className={`${ghost} ${autoAlias ? 'bg-hover' : ''}`}
        title="Alias automático ao aceitar uma tabela depois de FROM ou JOIN"
        aria-pressed={autoAlias}
        onClick={onToggleAlias}
      >
        Alias {autoAlias ? 'ligado' : 'desligado'}
      </button>

      <div className="ml-auto flex min-w-0 items-center gap-3 text-[15px] text-muted">
        {tab.connectionId === null ? (
          <button className={ghost} onClick={onPickConnection}>Escolher conexão…</button>
        ) : (
          <>
            {connected ? (
              <button className={ghost} onClick={onDisconnect}>Desconectar</button>
            ) : (
              tab.status !== 'connecting' && <button className={ghost} onClick={onConnect}>Conectar</button>
            )}
            <span className="min-w-0 truncate">{connection ? `${connection.name} · ${connection.settings.server}` : ''}</span>
          </>
        )}
      </div>
    </div>
  )
}
