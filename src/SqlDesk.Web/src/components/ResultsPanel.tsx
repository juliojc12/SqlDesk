import { useState } from 'react'

/** Esqueleto do painel de resultados (sub-abas e exportação); o conteúdo chega com a execução. */
export function ResultsPanel({ color }: { color: string }) {
  const [active, setActive] = useState<'results' | 'messages'>('results')
  const tab = (k: 'results' | 'messages', label: string) => (
    <button
      role="tab"
      aria-selected={active === k}
      onClick={() => setActive(k)}
      style={active === k ? { borderBottomColor: color } : undefined}
      className={`-mb-px border-b-2 px-1 py-2 text-[15px] ${active === k ? 'border-b-2 text-fg' : 'border-transparent text-muted'}`}
    >
      {label}
    </button>
  )
  const ghost = 'flex h-8 items-center gap-2 rounded-md px-2 text-sm text-fg disabled:opacity-40'
  return (
    <section className="flex h-full min-h-0 flex-col bg-surface" aria-label="Resultados">
      <div className="flex h-11 shrink-0 items-center gap-5 border-b border-line px-5">
        <div role="tablist" className="flex gap-5">
          {tab('results', 'Resultados')}
          {tab('messages', 'Mensagens')}
        </div>
        <div className="ml-auto flex gap-2">
          <button disabled className={ghost}><span className="icon">&#xE896;</span> CSV</button>
          <button disabled className={ghost}><span className="icon">&#xE896;</span> XLSX</button>
        </div>
      </div>
      <div className="flex flex-1 items-center justify-center text-sm text-muted">
        {active === 'results' ? 'Nenhum resultado ainda.' : 'Nenhuma mensagem.'}
      </div>
    </section>
  )
}
