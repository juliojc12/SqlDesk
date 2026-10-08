/**
 * Botão liga/desliga da barra de ferramentas. Ligado, fica com borda e fundo verdes e o "interruptor" à direita; desligado,
 * fica neutro com o interruptor à esquerda. O estado se lê pela cor, pela posição do interruptor e pelo texto.
 */
export function Toggle({ on, label, title, onClick }: { on: boolean; label: string; title: string; onClick: () => void }) {
  return (
    <button
      role="switch"
      aria-checked={on}
      title={title}
      onClick={onClick}
      className={`flex h-8 shrink-0 items-center gap-1.5 whitespace-nowrap rounded-md border px-2 text-sm ${
        on ? 'border-emerald-500 bg-emerald-500/15 font-medium text-emerald-300 hover:bg-emerald-500/25' : 'border-line text-muted hover:bg-hover'
      }`}
    >
      <span aria-hidden className={`relative h-4 w-7 shrink-0 rounded-full transition-colors ${on ? 'bg-emerald-500' : 'bg-line'}`}>
        <span className={`absolute top-0.5 h-3 w-3 rounded-full bg-white shadow transition-all ${on ? 'left-3.5' : 'left-0.5'}`} />
      </span>
      {label}
    </button>
  )
}
