import { useEffect, useState } from 'react'
import { BridgeCallError, invoke } from '../bridge'
import type { AiProviderId } from '../contracts'
import { DEFAULT_SETTINGS, MAX_ROWS, MAX_TIMEOUT, MIN_ROWS, normalize, type AppSettings } from '../settings'
import { btnBase, btnPrimary, Modal } from './Modal'

const AI_PROVIDERS: { id: AiProviderId; label: string; defaultModel: string }[] = [
  { id: 'anthropic', label: 'Claude (Anthropic)', defaultModel: 'claude-haiku-5-5' },
  { id: 'openai', label: 'ChatGPT (OpenAI)', defaultModel: 'gpt-4o-mini' },
  { id: 'gemini', label: 'Gemini (Google)', defaultModel: 'gemini-2.0-flash' },
]

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

  // Consulta com IA: provedor, modelo e chave vivem no backend (a chave nunca volta para cá, só se existe).
  const [aiProvider, setAiProvider] = useState<AiProviderId>('anthropic')
  const [aiModel, setAiModel] = useState('')
  const [aiKey, setAiKey] = useState('')
  const [aiHasKey, setAiHasKey] = useState(false)
  const [aiRemoveKey, setAiRemoveKey] = useState(false)
  const [aiLoaded, setAiLoaded] = useState(false)
  const [aiError, setAiError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    let alive = true
    invoke('ai.settings.get', {})
      .then((r) => {
        if (!alive) return
        setAiProvider(r.provider)
        setAiModel(r.model)
        setAiHasKey(r.hasKey)
        setAiLoaded(true)
      })
      .catch(() => alive && setAiLoaded(false))
    return () => { alive = false }
  }, [])

  const draft = normalize({ maxRows, commandTimeout: timeout, autoAlias, csvDelimiter: delimiter, aiEnabled: settings.aiEnabled })

  async function submit() {
    setAiError(null)
    if (aiLoaded) {
      setSaving(true)
      try {
        await invoke('ai.settings.save', { provider: aiProvider, model: aiModel.trim(), apiKey: aiKey.trim() || null, removeKey: aiRemoveKey })
      } catch (e) {
        setAiError(e instanceof BridgeCallError ? e.detail.message : String(e))
        setSaving(false)
        return
      }
      setSaving(false)
    }
    onSave(draft)
  }
  const adjusted = String(draft.maxRows) !== maxRows.trim() || String(draft.commandTimeout) !== timeout.trim()

  return (
    <Modal title="Configurações" onCancel={onCancel} width={520}>
      <form
        className="p-5"
        onSubmit={(e) => {
          e.preventDefault()
          void submit()
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

          <fieldset className="space-y-3 rounded-md border border-line p-3">
            <legend className="px-1 font-medium">Consulta com IA</legend>
            {!aiLoaded ? (
              <p className="text-xs text-muted">Indisponível: o backend não respondeu.</p>
            ) : (
              <>
                <label className="block">
                  <span>Provedor</span>
                  <select
                    className={`${input} mt-1`}
                    value={aiProvider}
                    aria-label="Provedor de IA"
                    onChange={(e) => {
                      const next = e.target.value as AiProviderId
                      // Trocar de provedor com o modelo padrão do anterior: acompanha o padrão do novo.
                      if (AI_PROVIDERS.some((p) => p.id === aiProvider && p.defaultModel === aiModel)) setAiModel(AI_PROVIDERS.find((p) => p.id === next)!.defaultModel)
                      setAiProvider(next)
                    }}
                  >
                    {AI_PROVIDERS.map((p) => <option key={p.id} value={p.id}>{p.label}</option>)}
                  </select>
                </label>
                <label className="block">
                  <span>Modelo</span>
                  <input className={`${input} mt-1`} value={aiModel} onChange={(e) => setAiModel(e.target.value)} aria-label="Modelo de IA"
                    placeholder={AI_PROVIDERS.find((p) => p.id === aiProvider)?.defaultModel} spellCheck={false} />
                </label>
                <label className="block">
                  <span>Chave de API</span>
                  <input
                    className={`${input} mt-1`}
                    type="password"
                    autoComplete="off"
                    value={aiKey}
                    aria-label="Chave de API"
                    placeholder={aiHasKey && !aiRemoveKey ? 'Chave salva (digite para trocar)' : 'Cole a chave aqui'}
                    onChange={(e) => { setAiKey(e.target.value); setAiRemoveKey(false) }}
                  />
                </label>
                {aiHasKey && (
                  <label className="flex items-center gap-2 text-xs">
                    <input type="checkbox" checked={aiRemoveKey} onChange={(e) => { setAiRemoveKey(e.target.checked); if (e.target.checked) setAiKey('') }} />
                    Remover a chave salva
                  </label>
                )}
                <p className="text-xs text-muted">
                  A chave fica protegida no seu usuário do Windows e só o app a lê. Ao gerar uma consulta, o pedido e os nomes das tabelas, colunas e tipos
                  do banco são enviados ao provedor escolhido; o conteúdo das linhas nunca é enviado. O SQL gerado só é aceito se for uma consulta de
                  leitura e nunca é executado sozinho.
                </p>
              </>
            )}
            {aiError && <p role="alert" className="rounded-md bg-red-200 px-3 py-2 text-sm text-red-950">{aiError}</p>}
          </fieldset>
        </div>

        {adjusted && (
          <p role="note" className="mt-3 rounded-md bg-amber-500/20 px-3 py-2 text-sm">
            Valor fora da faixa ou inválido: será salvo como {draft.maxRows.toLocaleString('pt-BR')} linhas e {draft.commandTimeout} s.
          </p>
        )}

        <div className="mt-5 flex justify-end gap-2">
          <button type="button" className={btnBase} onClick={onCancel}>Cancelar</button>
          <button type="submit" className={btnPrimary} disabled={saving}>Salvar</button>
        </div>
      </form>
    </Modal>
  )
}
