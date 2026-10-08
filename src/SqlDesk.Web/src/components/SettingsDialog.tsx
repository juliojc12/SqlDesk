import { useEffect, useState } from 'react'
import { BridgeCallError, invoke } from '../bridge'
import type { AiProviderEntry, AiProviderId } from '../contracts'
import { DEFAULT_SETTINGS, MAX_ROWS, MAX_TIMEOUT, MIN_ROWS, normalize, type AppSettings } from '../settings'
import { btnBase, btnPrimary, Modal } from './Modal'

interface AiProviderMeta {
  id: AiProviderId
  label: string
  defaultModel: string
  /** Falso: modelo local, a chave é opcional. */
  needsKey: boolean
  /** O usuário informa o endereço da API. */
  customUrl?: boolean
  /** Onde conseguir a chave. */
  hint?: string
}

const AI_PROVIDERS: AiProviderMeta[] = [
  { id: 'anthropic', label: 'Claude (Anthropic)', defaultModel: 'claude-haiku-5-5', needsKey: true },
  { id: 'openai', label: 'ChatGPT (OpenAI)', defaultModel: 'gpt-4o-mini', needsKey: true },
  { id: 'gemini', label: 'Gemini (Google)', defaultModel: 'gemini-2.0-flash', needsKey: true, hint: 'A chave gratuita sai no Google AI Studio.' },
  { id: 'nvidia', label: 'NVIDIA (build.nvidia.com)', defaultModel: 'meta/llama-3.3-70b-instruct', needsKey: true, hint: 'Há modelos gratuitos: crie a chave (nvapi-…) em build.nvidia.com. Use o nome do modelo como na página dele (ex.: nvidia/nemotron-3-super-120b-a12b); roteadores como o OmniRoute acrescentam um prefixo que aqui não vale.' },
  { id: 'groq', label: 'Groq', defaultModel: 'llama-3.3-70b-versatile', needsKey: true, hint: 'Tem plano gratuito (console.groq.com).' },
  { id: 'openrouter', label: 'OpenRouter', defaultModel: 'meta-llama/llama-3.3-70b-instruct:free', needsKey: true, hint: 'Modelos com final ":free" não têm custo (openrouter.ai).' },
  { id: 'cerebras', label: 'Cerebras', defaultModel: 'llama-3.3-70b', needsKey: true, hint: 'Tem plano gratuito (cloud.cerebras.ai). Confira o nome do modelo disponível na sua conta.' },
  { id: 'mistral', label: 'Mistral', defaultModel: 'mistral-small-latest', needsKey: true, hint: 'O plano gratuito pode usar seus dados para treinar modelos.' },
  { id: 'ollama', label: 'Ollama (modelo local)', defaultModel: 'llama3.1', needsKey: false, hint: 'Roda na sua máquina, sem enviar nada para a internet. Sem chave.' },
  { id: 'custom', label: 'Outro (compatível com OpenAI)', defaultModel: '', needsKey: false, customUrl: true, hint: 'Qualquer servidor com /chat/completions no formato da OpenAI.' },
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
  const [aiBaseUrl, setAiBaseUrl] = useState('')
  const [aiEntries, setAiEntries] = useState<AiProviderEntry[]>([])
  const [aiKey, setAiKey] = useState('')
  const [aiHasKey, setAiHasKey] = useState(false)
  const [aiRemoveKey, setAiRemoveKey] = useState(false)
  const [aiLoaded, setAiLoaded] = useState(false)
  const [aiError, setAiError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [testing, setTesting] = useState(false)
  const [testResult, setTestResult] = useState<{ ok: boolean; message: string } | null>(null)

  useEffect(() => {
    let alive = true
    invoke('ai.settings.get', {})
      .then((r) => {
        if (!alive) return
        setAiEntries(r.entries)
        setAiProvider(r.provider)
        setAiModel(r.model)
        setAiBaseUrl(r.baseUrl ?? '')
        setAiHasKey(r.hasKey)
        setAiLoaded(true)
      })
      .catch(() => alive && setAiLoaded(false))
    return () => { alive = false }
  }, [])

  const meta = AI_PROVIDERS.find((p) => p.id === aiProvider) ?? AI_PROVIDERS[0]

  /** Cada provedor guarda o seu modelo, endereço e chave: ao trocar, a tela mostra o que ele já tinha (ou o padrão). */
  function selectProvider(next: AiProviderId) {
    const saved = aiEntries.find((e) => e.provider === next)
    setAiProvider(next)
    setAiModel(saved?.model || AI_PROVIDERS.find((p) => p.id === next)!.defaultModel)
    setAiBaseUrl(saved?.baseUrl ?? '')
    setAiHasKey(saved?.hasKey ?? false)
    setAiKey('')
    setAiRemoveKey(false)
    setTestResult(null)
  }

  /** Pedido mínimo ao provedor com a chave digitada (ou a guardada) e o modelo da tela, sem salvar nada. */
  async function testAi() {
    setTesting(true)
    setTestResult(null)
    try {
      setTestResult(await invoke('ai.test', { provider: aiProvider, model: aiModel.trim(), baseUrl: meta.customUrl ? aiBaseUrl.trim() : null, apiKey: aiKey.trim() || null }))
    } catch (e) {
      setTestResult({ ok: false, message: e instanceof BridgeCallError ? e.detail.message : String(e) })
    } finally {
      setTesting(false)
    }
  }

  const draft = normalize({ maxRows, commandTimeout: timeout, autoAlias, csvDelimiter: delimiter, aiEnabled: settings.aiEnabled })

  async function submit() {
    setAiError(null)
    if (aiLoaded) {
      setSaving(true)
      try {
        await invoke('ai.settings.save', { provider: aiProvider, model: aiModel.trim(), baseUrl: meta.customUrl ? aiBaseUrl.trim() : null, apiKey: aiKey.trim() || null, removeKey: aiRemoveKey })
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
                    onChange={(e) => selectProvider(e.target.value as AiProviderId)}
                  >
                    {AI_PROVIDERS.map((p) => <option key={p.id} value={p.id}>{p.label}</option>)}
                  </select>
                </label>
                {meta.hint && <p className="text-xs text-muted">{meta.hint}</p>}
                {meta.customUrl && (
                  <label className="block">
                    <span>Endereço da API</span>
                    <input className={`${input} mt-1`} value={aiBaseUrl} onChange={(e) => setAiBaseUrl(e.target.value)} aria-label="Endereço da API"
                      placeholder="https://servidor/v1" spellCheck={false} />
                    <span className="mt-1 block text-xs text-muted">Só https (ou http na sua própria máquina). A chave e o pedido são enviados para este endereço.</span>
                  </label>
                )}
                <label className="block">
                  <span>Modelo</span>
                  <input className={`${input} mt-1`} value={aiModel} onChange={(e) => { setAiModel(e.target.value); setTestResult(null) }} aria-label="Modelo de IA"
                    placeholder={meta.defaultModel || 'nome do modelo'} spellCheck={false} />
                </label>
                <label className="block">
                  <span>Chave de API{meta.needsKey ? '' : ' (opcional)'}</span>
                  <input
                    className={`${input} mt-1`}
                    type="password"
                    autoComplete="off"
                    value={aiKey}
                    aria-label="Chave de API"
                    placeholder={aiHasKey && !aiRemoveKey ? 'Chave salva (digite para trocar)' : meta.needsKey ? 'Cole a chave aqui' : 'Deixe em branco se não precisar'}
                    onChange={(e) => { setAiKey(e.target.value); setAiRemoveKey(false); setTestResult(null) }}
                  />
                </label>
                <div className="flex items-start gap-3">
                  <button type="button" className={btnBase} disabled={testing || saving} onClick={() => void testAi()}>
                    {testing ? 'Testando…' : 'Testar chave e modelo'}
                  </button>
                  {testResult && (
                    <p role="status" className={`min-w-0 flex-1 rounded-md px-3 py-2 text-xs ${testResult.ok ? 'bg-green-200 text-green-950' : 'bg-red-200 text-red-950'}`}>
                      {testResult.message}
                    </p>
                  )}
                </div>
                {aiHasKey && (
                  <label className="flex items-center gap-2 text-xs">
                    <input type="checkbox" checked={aiRemoveKey} onChange={(e) => { setAiRemoveKey(e.target.checked); if (e.target.checked) setAiKey('') }} />
                    Remover a chave salva
                  </label>
                )}
                <p className="text-xs text-muted">
                  A chave fica protegida no seu usuário do Windows e só o app a lê. Ao gerar uma consulta, o pedido e os nomes das tabelas, colunas e tipos
                  do banco são enviados ao provedor escolhido (nos planos gratuitos, alguns provedores podem usar esses textos para treinar modelos); o conteúdo das linhas nunca é enviado. O SQL gerado só é aceito se for uma consulta de
                  leitura e é executado direto, sem aparecer no editor; qualquer outra coisa (alterar dados ou estrutura) nunca é executada.
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
