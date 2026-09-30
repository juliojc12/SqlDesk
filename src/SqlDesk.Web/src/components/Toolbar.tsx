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
}

const ghost = 'flex h-8 items-center gap-2 rounded-md px-3 text-sm text-fg enabled:hover:bg-hover disabled:opacity-40'

export function Toolbar({ tab, connection, color, onSave, onOpen, onConnect, onDisconnect, onPickConnection }: Props) {
  const connected = tab.status === 'connected'
  return (
    <div className="flex h-14 shrink-0 items-center gap-2 border-b border-line bg-surface px-4">
      {/* Execução e transações chegam nas fases seguintes; os controles já ocupam seu lugar no layout. */}
      <button
        disabled
        title="Executar (Ctrl+Enter)"
        style={{ backgroundColor: color, color: textOn(color) }}
        className="flex h-9 items-center gap-2 rounded-lg px-4 text-[15px] font-medium opacity-50"
      >
        <span className="icon">&#xE768;</span> Executar
      </button>
      <button disabled title="Parar (Esc)" aria-label="Parar" className={`${ghost} icon w-9 justify-center px-0`}>&#xE71A;</button>
      <button disabled className={ghost}>
        <span className="icon">&#xE8C8;</span> Transação
      </button>
      <span className="mx-1 h-5 w-px bg-line" aria-hidden />
      <button className={`${ghost} icon w-9 justify-center px-0`} title="Salvar (Ctrl+S)" aria-label="Salvar" onClick={onSave}>&#xE74E;</button>
      <button className={`${ghost} icon w-9 justify-center px-0`} title="Abrir arquivo .sql (Ctrl+O)" aria-label="Abrir arquivo" onClick={onOpen}>&#xE8E5;</button>

      <div className="ml-auto flex items-center gap-3 text-[15px] text-muted">
        {tab.connectionId === null ? (
          <button className={ghost} onClick={onPickConnection}>Escolher conexão…</button>
        ) : (
          <>
            {connected ? (
              <button className={ghost} onClick={onDisconnect}>Desconectar</button>
            ) : (
              tab.status !== 'connecting' && <button className={ghost} onClick={onConnect}>Conectar</button>
            )}
            <span>{connection ? `${connection.name} · ${connection.settings.server}` : ''}</span>
          </>
        )}
      </div>
    </div>
  )
}
