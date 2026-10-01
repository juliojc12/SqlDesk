import { useEffect, useState } from 'react'
import { invoke, on } from '../bridge'

const btn = 'no-drag icon flex h-full w-12 items-center justify-center text-fg hover:bg-hover'

export function TitleBar({ onToggleSidebar, onOpenSettings }: { onToggleSidebar: () => void; onOpenSettings: () => void }) {
  const [maximized, setMaximized] = useState(false)
  useEffect(() => {
    const off = on<{ maximized: boolean }>('window.state', (s) => setMaximized(s.maximized))
    return () => {
      off()
    }
  }, [])

  return (
    <header className="drag flex h-10 shrink-0 items-center border-b border-line bg-surface" onDoubleClick={() => void invoke('window.toggleMaximize', {})}>
      <button
        className="no-drag icon ml-2 flex h-8 w-8 items-center justify-center rounded text-fg hover:bg-hover"
        title="Mostrar/ocultar painel (Ctrl+B)"
        aria-label="Mostrar ou ocultar o painel lateral"
        onClick={onToggleSidebar}
        onDoubleClick={(e) => e.stopPropagation()}
      >
        &#xE700;
      </button>
      <img src="/favicon.svg" alt="" aria-hidden className="ml-3 h-5 w-5 select-none" draggable={false} />
      <span className="ml-2 text-sm font-semibold">Julião SQLStudio</span>
      <div className="ml-auto flex h-full" onDoubleClick={(e) => e.stopPropagation()}>
        <button className={btn} title="Configurações (Ctrl+,)" aria-label="Configurações" onClick={onOpenSettings}>&#xE713;</button>
        <button className={btn} aria-label="Minimizar" onClick={() => void invoke('window.minimize', {})}>&#xE921;</button>
        <button className={btn} aria-label={maximized ? 'Restaurar' : 'Maximizar'} onClick={() => void invoke('window.toggleMaximize', {})}>
          {maximized ? '' : ''}
        </button>
        <button className={`${btn} hover:!bg-red-600 hover:!text-white`} aria-label="Fechar" onClick={() => void invoke('window.close', {})}>&#xE8BB;</button>
      </div>
    </header>
  )
}
