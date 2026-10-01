import { useEffect, useState } from 'react'
import { formatElapsed } from '../results'

/** Tempo decorrido desde `since`, atualizado a cada 100 ms. */
function useElapsed(since: number | null): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (since === null) return
    const h = window.setInterval(() => setNow(Date.now()), 100)
    return () => window.clearInterval(h)
  }, [since])
  return since === null ? 0 : Math.max(0, now - since)
}

/** Texto do contador, no mesmo formato da barra de status ("820 ms", "3,40 s", "1 min 5 s"). */
export const elapsedLabel = (ms: number) => formatElapsed(Math.floor(ms / 100) * 100)

/**
 * Área de resultados enquanto a query roda e ainda não chegou nada: giroflex, tempo decorrido e como cancelar.
 * Some sozinho quando o primeiro resultado aparece.
 */
export function RunningIndicator({ since, color, onStop }: { since: number | null; color: string; onStop: () => void }) {
  const elapsed = useElapsed(since)
  return (
    <div role="status" aria-live="polite" aria-label="Executando a consulta" className="flex h-full flex-col items-center justify-center gap-3 text-sm">
      <span
        aria-hidden
        className="h-8 w-8 animate-spin rounded-full border-[3px] border-line motion-reduce:animate-none"
        style={{ borderTopColor: color }}
      />
      <p className="font-medium text-fg">Executando a consulta…</p>
      <p className="tabular-nums text-muted" aria-label={`Tempo decorrido: ${elapsedLabel(elapsed)}`}>{elapsedLabel(elapsed)}</p>
      <p className="text-xs text-muted">
        Pressione <kbd className="rounded border border-line px-1">Esc</kbd> ou{' '}
        <button className="underline decoration-dotted hover:decoration-solid" onClick={onStop}>
          clique aqui para cancelar
        </button>
      </p>
    </div>
  )
}

/** Faixa fina e indeterminada sob as abas do resultado: indica execução em andamento mesmo com linhas já na tela. */
export function RunningBar({ color }: { color: string }) {
  return (
    <div role="progressbar" aria-label="Executando" className="relative h-0.5 shrink-0 overflow-hidden bg-line">
      <div className="running-bar absolute inset-y-0 w-1/3" style={{ backgroundColor: color }} />
    </div>
  )
}
