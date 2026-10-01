import { useState } from 'react'
import { DEFAULT_SETTINGS, MAX_ROWS, MAX_TIMEOUT, MIN_ROWS, normalize, type AppSettings } from '../settings'
import { btnBase, btnPrimary, Modal } from './Modal'

const input = 'w-full rounded-md border border-line bg-input px-2 py-1.5 text-sm'

/** Configurações do app. Os campos numéricos aceitam texto livre e são ajustados à faixa permitida ao salvar. */
export function SettingsDialog({ settings, onSave, onCancel }: {
  settings: AppSettings
  onSave: (s: AppSettings) => void
  onCancel: () => void
}) {
  const [maxRows, setMaxRows] = useState(String(settings.maxRows))
  const [timeout, setTimeoutText] = useState(String(settings.commandTimeout))
  const [autoAlias, setAutoAlias] = useState(settings.autoAlias)
  const [delimiter, setDelimiter] = useState(settings.csvDelimiter)

  const draft = normalize({ maxRows, commandTimeout: timeout, autoAlias, csvDelimiter: delimiter })
  const adjusted = String(draft.maxRows) !== maxRows.trim() || String(draft.commandTimeout) !== timeout.trim()

  return (
    <Modal title="Configurações" onCancel={onCancel} width={520}>
      <form
        className="p-5"
        onSubmit={(e) => {
          e.preventDefault()
          onSave(draft)
        }}
      >
        <h2 className="text-base font-semibold">Configurações</h2>
        <div className="mt-4 space-y-4 text-sm">
          <label className="block">
            <span className="font-medium">Limite de linhas na grade</span>
            <input className={`${input} mt-1`} inputMode="numeric" value={maxRows} onChange={(e) => setMaxRows(e.target.value)} aria-label="Limite de linhas na grade" />
            <span className="mt-1 block text-xs text-muted">
              Linhas carregadas por resultado ({MIN_ROWS.toLocaleString('pt-BR')} a {MAX_ROWS.toLocaleString('pt-BR')}; padrão {DEFAULT_SETTINGS.maxRows.toLocaleString('pt-BR')}).
              Vale para as próximas execuções; “Carregar todas” e a exportação ignoram o limite.
            </span>
          </label>

          <label className="flex items-start gap-3">
            <input type="checkbox" className="mt-1" checked={autoAlias} onChange={(e) => setAutoAlias(e.target.checked)} />
            <span>
              <span className="font-medium">Alias automático de tabela</span>
              <span className="block text-xs text-muted">Ao aceitar uma tabela depois de FROM ou JOIN, insere um alias (Clientes vira c).</span>
            </span>
          </label>

          <label className="block">
            <span className="font-medium">Separador do CSV</span>
            <select className={`${input} mt-1`} value={delimiter} onChange={(e) => setDelimiter(e.target.value === ',' ? ',' : ';')} aria-label="Separador do CSV">
              <option value=";">Ponto e vírgula (;) — padrão para o Excel em português</option>
              <option value=",">Vírgula (,)</option>
            </select>
          </label>

          <label className="block">
            <span className="font-medium">Timeout de comando para novas conexões (segundos)</span>
            <input className={`${input} mt-1`} inputMode="numeric" value={timeout} onChange={(e) => setTimeoutText(e.target.value)} aria-label="Timeout de comando" />
            <span className="mt-1 block text-xs text-muted">
              0 = sem limite (máximo {MAX_TIMEOUT.toLocaleString('pt-BR')}). Cada conexão guarda o seu próprio timeout; isto só define o valor inicial de uma conexão nova.
            </span>
          </label>
        </div>

        {adjusted && (
          <p role="note" className="mt-3 rounded-md bg-amber-500/20 px-3 py-2 text-sm">
            Valor fora da faixa ou inválido: será salvo como {draft.maxRows.toLocaleString('pt-BR')} linhas e {draft.commandTimeout} s.
          </p>
        )}

        <div className="mt-5 flex justify-end gap-2">
          <button type="button" className={btnBase} onClick={onCancel}>Cancelar</button>
          <button type="submit" className={btnPrimary}>Salvar</button>
        </div>
      </form>
    </Modal>
  )
}
