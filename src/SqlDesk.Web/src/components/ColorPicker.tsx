import { PALETTE } from '../colors'

export function ColorPicker({ value, onChange }: { value: string; onChange: (c: string) => void }) {
  return (
    <div className="flex flex-wrap items-center gap-1.5">
      {PALETTE.map((c) => (
        <button
          key={c}
          type="button"
          aria-label={`Cor ${c}`}
          aria-pressed={value.toLowerCase() === c.toLowerCase()}
          onClick={() => onChange(c)}
          style={{ backgroundColor: c }}
          className={`h-6 w-6 rounded-full border-2 ${value.toLowerCase() === c.toLowerCase() ? 'border-neutral-900 dark:border-white' : 'border-transparent'}`}
        />
      ))}
      <label className="ml-1 flex items-center gap-1 text-xs text-neutral-500">
        Outra
        <input type="color" value={value} onChange={(e) => onChange(e.target.value.toUpperCase())} className="h-6 w-8 cursor-pointer bg-transparent" />
      </label>
    </div>
  )
}
