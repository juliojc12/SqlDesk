import { useEffect, useRef, useState } from 'react'
import { BridgeCallError, invoke } from '../bridge'
import { PALETTE } from '../colors'
import type { ConnectionInfo, ConnectionSettings, TestConnectionResult } from '../contracts'
import { ColorPicker } from './ColorPicker'
import { btnBase, btnPrimary, Modal } from './Modal'

const EMPTY: ConnectionSettings = {
  server: '', database: '', user: '', connectTimeout: 15, commandTimeout: 30,
  encrypt: true, trustServerCertificate: false, advanced: {},
}

const input = 'w-full rounded-md border border-line bg-input px-2 py-1.5 text-sm'

function errorText(e: unknown): string {
  return e instanceof BridgeCallError ? e.detail.message : String(e)
}

interface Props {
  connection: ConnectionInfo | null // null = nova
  defaultColor: string
  onSaved: (c: ConnectionInfo) => void
  onClose: () => void
}

export function ConnectionDialog({ connection, defaultColor, onSaved, onClose }: Props) {
  const [name, setName] = useState(connection?.name ?? '')
  const [color, setColor] = useState(connection?.color ?? defaultColor)
  const [settings, setSettings] = useState<ConnectionSettings>(connection?.settings ?? EMPTY)
  const [password, setPassword] = useState('')
  const [tab, setTab] = useState<'fields' | 'string'>('fields')
  const [connString, setConnString] = useState('')
  const [stringError, setStringError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [test, setTest] = useState<TestConnectionResult | 'running' | null>(null)
  const [saving, setSaving] = useState(false)
  const parseTimer = useRef<number | undefined>(undefined)

  useEffect(() => () => window.clearTimeout(parseTimer.current), [])

  const patch = (p: Partial<ConnectionSettings>) => setSettings((s) => ({ ...s, ...p }))

  async function regenerateString() {
    try {
      const r = await invoke('connections.build', { settings, password })
      setConnString(r.connectionString)
      setStringError(null)
    } catch (e) {
      setStringError(errorText(e))
    }
  }

  /** Aplica a connection string aos campos. Devolve false se inválida (campos ficam intactos). */
  async function applyString(text: string): Promise<boolean> {
    try {
      const r = await invoke('connections.parse', { connectionString: text })
      setSettings(r.settings)
      if (r.password) setPassword(r.password)
      setStringError(null)
      return true
    } catch (e) {
      setStringError(errorText(e))
      return false
    }
  }

  async function switchTab(next: 'fields' | 'string') {
    if (next === tab) return
    if (next === 'string') {
      await regenerateString()
      setTab('string')
    } else if (await applyString(connString)) {
      setTab('fields')
    }
  }

  function onStringChange(text: string) {
    setConnString(text)
    window.clearTimeout(parseTimer.current)
    parseTimer.current = window.setTimeout(() => void applyString(text), 400)
  }

  async function runTest() {
    setTest('running')
    try {
      setTest(await invoke('connections.test', { id: connection?.id ?? null, settings, password }))
    } catch (e) {
      setTest({ ok: false, errorMessage: errorText(e) })
    }
  }

  async function save() {
    setError(null)
    if (tab === 'string' && !(await applyString(connString))) return
    setSaving(true)
    try {
      onSaved(await invoke('connections.save', { id: connection?.id ?? null, name, color, settings, password: password || null }))
    } catch (e) {
      setError(errorText(e))
      setSaving(false)
    }
  }

  const advancedKeys = Object.keys(settings.advanced)

  return (
    <Modal title={connection ? 'Editar conexão' : 'Nova conexão'} onCancel={onClose} width={560}>
      <div className="flex max-h-[90vh] flex-col">
        <h2 className="border-b border-line px-5 py-3 text-base font-semibold">
          {connection ? 'Editar conexão' : 'Nova conexão'}
        </h2>

        <div className="space-y-3 overflow-y-auto px-5 py-4 text-sm">
          <label className="block">
            Nome
            <input className={input} value={name} onChange={(e) => setName(e.target.value)} autoFocus />
          </label>
          <div>
            <div className="mb-1">Cor</div>
            <ColorPicker value={color} onChange={setColor} />
          </div>

          <div role="tablist" className="flex gap-4 border-b border-line">
            {([['fields', 'Campos'], ['string', 'Connection string']] as const).map(([k, label]) => (
              <button
                key={k}
                role="tab"
                aria-selected={tab === k}
                onClick={() => void switchTab(k)}
                className={`-mb-px border-b-2 px-1 py-1.5 ${tab === k ? 'border-blue-600 font-medium' : 'border-transparent text-muted'}`}
              >
                {label}
              </button>
            ))}
          </div>

          {tab === 'fields' ? (
            <div className="space-y-3">
              <label className="block">
                Servidor <span className="text-muted">(host ou host,porta)</span>
                <input className={input} value={settings.server} onChange={(e) => patch({ server: e.target.value })} />
              </label>
              <label className="block">
                Banco
                <input className={input} value={settings.database} onChange={(e) => patch({ database: e.target.value })} />
              </label>
              <div className="grid grid-cols-2 gap-3">
                <label className="block">
                  Usuário
                  <input className={input} value={settings.user} onChange={(e) => patch({ user: e.target.value })} autoComplete="off" />
                </label>
                <label className="block">
                  Senha
                  <input
                    type="password"
                    className={input}
                    value={password}
                    placeholder={connection?.hasPassword ? '•••••• (salva)' : ''}
                    onChange={(e) => setPassword(e.target.value)}
                    autoComplete="new-password"
                  />
                </label>
              </div>
              <div className="grid grid-cols-2 gap-3">
                <label className="block">
                  Timeout de conexão (s)
                  <input type="number" min={0} className={input} value={settings.connectTimeout} onChange={(e) => patch({ connectTimeout: Number(e.target.value) })} />
                </label>
                <label className="block">
                  Timeout de comando (s)
                  <input type="number" min={0} className={input} value={settings.commandTimeout} onChange={(e) => patch({ commandTimeout: Number(e.target.value) })} />
                </label>
              </div>
              <div className="flex gap-6">
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={settings.encrypt} onChange={(e) => patch({ encrypt: e.target.checked })} /> Encrypt
                </label>
                <label className="flex items-center gap-2">
                  <input type="checkbox" checked={settings.trustServerCertificate} onChange={(e) => patch({ trustServerCertificate: e.target.checked })} /> TrustServerCertificate
                </label>
              </div>
              {advancedKeys.length > 0 && (
                <div className="rounded-md bg-hover p-2 text-xs">
                  <div className="mb-1 font-medium">Opções avançadas (preservadas)</div>
                  {advancedKeys.map((k) => (
                    <div key={k} className="font-mono">{k}={settings.advanced[k]}</div>
                  ))}
                </div>
              )}
            </div>
          ) : (
            <div>
              <textarea
                className={`${input} h-32 font-mono`}
                spellCheck={false}
                value={connString}
                onChange={(e) => onStringChange(e.target.value)}
              />
              {stringError && <p className="mt-1 text-xs text-danger">{stringError}</p>}
              <p className="mt-1 text-xs text-muted">
                {connection?.hasPassword && !password && 'A senha salva não é exibida. Deixe sem Password para mantê-la. '}
                O timeout de comando não faz parte da connection string; ajuste-o na aba Campos.
              </p>
            </div>
          )}

          {test && (
            <p role="status" className={`rounded-md p-2 text-xs ${test === 'running' ? 'bg-hover' : test.ok ? 'bg-green-200 text-green-950' : 'bg-red-200 text-red-950'}`}>
              {test === 'running' ? 'Testando…' : test.ok ? `Conexão bem-sucedida. SQL Server ${test.serverVersion}` : test.errorMessage}
            </p>
          )}
          {error && <p role="alert" className="text-xs text-danger">{error}</p>}
        </div>

        <div className="flex items-center justify-between border-t border-line px-5 py-3">
          <button onClick={() => void runTest()} disabled={test === 'running'} className={btnBase}>
            Testar conexão
          </button>
          <div className="flex gap-2">
            <button onClick={onClose} className={btnBase}>Cancelar</button>
            <button onClick={() => void save()} disabled={saving} className={btnPrimary}>Salvar</button>
          </div>
        </div>
      </div>
    </Modal>
  )
}

export function nextDefaultColor(existing: ConnectionInfo[]): string {
  return PALETTE[existing.length % PALETTE.length]
}
