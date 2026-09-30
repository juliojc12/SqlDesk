import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from 'react'
import { BridgeCallError, invoke } from './bridge'
import { ConfirmDialog } from './components/ConfirmDialog'
import { ConnectionDialog, nextDefaultColor } from './components/ConnectionDialog'
import { EditorPane, modelPath } from './components/EditorPane'
import { ResultsPanel } from './components/ResultsPanel'
import { Sidebar } from './components/Sidebar'
import { StatusBar } from './components/StatusBar'
import { ConnectionPicker, PasswordPrompt, UnsavedDialog } from './components/TabDialogs'
import { TabBar } from './components/TabBar'
import { TitleBar } from './components/TitleBar'
import { Toolbar } from './components/Toolbar'
import { NEUTRAL_COLOR } from './colors'
import type { ConnectionInfo } from './contracts'
import { monaco } from './monacoSetup'
import { deserialize, initialTabsState, isDirty, serialize, tabsReducer, type Tab } from './tabsState'
import { useTheme } from './useTheme'

const msg = (e: unknown) => (e instanceof BridgeCallError ? e.detail.message : String(e))

function loadNumber(key: string, fallback: number): number {
  try {
    const v = Number(localStorage.getItem(key))
    return Number.isFinite(v) && v > 0 ? v : fallback
  } catch {
    return fallback
  }
}

function storeNumber(key: string, value: number) {
  try {
    localStorage.setItem(key, String(Math.round(value)))
  } catch {
    /* preferência de conveniência; sem armazenamento, segue com o padrão */
  }
}

type Picker = { purpose: 'new-tab' | 'open-file' | 'assign'; title: string; file?: { path: string; name: string; content: string }; tabId?: string }

export default function App() {
  const theme = useTheme()
  const [connections, setConnections] = useState<ConnectionInfo[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [tabsState, dispatch] = useReducer(tabsReducer, initialTabsState)
  const [booted, setBooted] = useState(false)
  const [editing, setEditing] = useState<{ connection: ConnectionInfo | null } | null>(null)
  const [deleting, setDeleting] = useState<ConnectionInfo | null>(null)
  const [closing, setClosing] = useState<Tab | null>(null)
  const [picker, setPicker] = useState<Picker | null>(null)
  const [promptTabId, setPromptTabId] = useState<string | null>(null)
  const [promptError, setPromptError] = useState<string | undefined>()
  const [error, setError] = useState<string | null>(null)
  const [sidebarOpen, setSidebarOpen] = useState(true)
  const [sidebarWidth, setSidebarWidth] = useState(() => loadNumber('sidebarWidth', 260))
  const [resultsHeight, setResultsHeight] = useState(() => loadNumber('resultsHeight', 260))

  const { tabs, activeId } = tabsState
  const activeTab = tabs.find((t) => t.id === activeId) ?? null
  const connById = useCallback((id: string | null) => connections.find((c) => c.id === id) ?? null, [connections])
  const activeConn = connById(activeTab?.connectionId ?? null)
  const activeColor = activeConn?.color ?? NEUTRAL_COLOR
  const connectedIds = useMemo(
    () => new Set(tabs.filter((t) => t.status === 'connected' && t.connectionId).map((t) => t.connectionId as string)),
    [tabs],
  )

  // ---------- Inicialização: conexões + restauração das abas ----------
  useEffect(() => {
    void (async () => {
      try {
        const list = (await invoke('connections.list', {})).connections
        setConnections(list)
        const saved = await invoke('session.load', {})
        dispatch({ type: 'restore', state: deserialize(saved.state, new Set(list.map((c) => c.id))) })
      } catch (e) {
        setError(msg(e))
      } finally {
        setBooted(true)
      }
    })()
  }, [])

  const reloadConnections = useCallback(async () => {
    try {
      const list = (await invoke('connections.list', {})).connections
      setConnections(list)
      return list
    } catch (e) {
      setError(msg(e))
      return null
    }
  }, [])

  // ---------- Persistência das abas (com debounce) ----------
  const stateJson = useMemo(() => serialize(tabsState), [tabsState])
  useEffect(() => {
    if (!booted) return
    const h = window.setTimeout(() => void invoke('session.save', { state: stateJson }).catch((e) => setError(msg(e))), 500)
    return () => window.clearTimeout(h)
  }, [stateJson, booted])

  // ---------- Conexão das abas ----------
  const connectTab = useCallback(async (tabId: string, connectionId: string, password?: string) => {
    dispatch({ type: 'setStatus', id: tabId, status: 'connecting' })
    try {
      const r = await invoke('tabs.open', { tabId, connectionId, password: password ?? null })
      dispatch({ type: 'setStatus', id: tabId, status: 'connected', serverVersion: r.serverVersion })
      setPromptTabId((cur) => (cur === tabId ? null : cur))
    } catch (e) {
      if (e instanceof BridgeCallError && e.detail.code === 'password_required') {
        dispatch({ type: 'setStatus', id: tabId, status: 'needs-password', message: 'Esta conexão não tem senha salva.' })
        if (password !== undefined) setPromptError(e.detail.message)
      } else {
        const untrusted = e instanceof BridgeCallError && e.detail.code === 'certificate_untrusted'
        dispatch({ type: 'setStatus', id: tabId, status: 'error', message: msg(e), certificateUntrusted: untrusted })
        setPromptTabId((cur) => (cur === tabId ? null : cur))
      }
    }
  }, [])

  // Conecta a aba ativa ao ser exibida. Sem senha salva, nunca tenta sozinho (a menos que outra aba já a tenha informado).
  useEffect(() => {
    if (!activeTab || activeTab.status !== 'idle' || !activeTab.connectionId) return
    const conn = connById(activeTab.connectionId)
    if (!conn) return
    if (conn.hasPassword || connectedIds.has(conn.id)) void connectTab(activeTab.id, conn.id)
    else dispatch({ type: 'setStatus', id: activeTab.id, status: 'needs-password', message: 'Esta conexão não tem senha salva.' })
  }, [activeTab, connById, connectedIds, connectTab])

  function requestConnect(tab: Tab) {
    if (!tab.connectionId) return
    const conn = connById(tab.connectionId)
    if (!conn) return
    if (conn.hasPassword || connectedIds.has(conn.id)) void connectTab(tab.id, conn.id)
    else {
      setPromptError(undefined)
      setPromptTabId(tab.id)
    }
  }

  /** Ação explícita do usuário no aviso da aba: grava TrustServerCertificate na conexão e reconecta. */
  async function trustCertificateAndReconnect(tab: Tab) {
    const conn = connById(tab.connectionId)
    if (!conn) return
    try {
      await invoke('connections.save', {
        id: conn.id, name: conn.name, color: conn.color, password: null,
        settings: { ...conn.settings, trustServerCertificate: true },
      })
      await reloadConnections()
      void connectTab(tab.id, conn.id)
    } catch (e) {
      setError(msg(e))
    }
  }

  async function disconnectTab(tab: Tab) {
    try {
      await invoke('tabs.disconnect', { tabId: tab.id })
      dispatch({ type: 'setStatus', id: tab.id, status: 'disconnected' })
    } catch (e) {
      setError(msg(e))
    }
  }

  // ---------- Abas ----------
  function addTab(conn: ConnectionInfo | null, extra: { text?: string; title?: string; filePath?: string | null } = {}) {
    const id = crypto.randomUUID()
    dispatch({ type: 'add', id, connectionId: conn?.id ?? null, ...extra })
    if (conn && !conn.hasPassword && !connectedIds.has(conn.id)) {
      // O usuário pediu esta aba agora: pede a senha na hora.
      dispatch({ type: 'setStatus', id, status: 'needs-password', message: 'Esta conexão não tem senha salva.' })
      setPromptError(undefined)
      setPromptTabId(id)
    }
  }

  function newTab() {
    const current = activeConn ?? connById(selectedId)
    if (current) addTab(current)
    else if (connections.length === 1) addTab(connections[0])
    else setPicker({ purpose: 'new-tab', title: 'Nova aba — escolha a conexão' })
  }

  function closeNow(tab: Tab) {
    dispatch({ type: 'close', id: tab.id })
    void invoke('tabs.disconnect', { tabId: tab.id }).catch((e) => setError(msg(e)))
    monaco.editor.getModel(monaco.Uri.parse(modelPath(tab.id)))?.dispose()
  }

  function requestClose(tab: Tab) {
    if (isDirty(tab)) setClosing(tab)
    else closeNow(tab)
  }

  async function saveTab(tab: Tab, saveAs = false): Promise<boolean> {
    try {
      const name = tab.title.toLowerCase().endsWith('.sql') ? tab.title : `${tab.title}.sql`
      const r = await invoke('files.save', { path: tab.filePath, suggestedName: name, content: tab.text, saveAs })
      if (r.cancelled) return false
      dispatch({ type: 'saved', id: tab.id, filePath: r.path ?? '', title: r.name ?? tab.title })
      return true
    } catch (e) {
      setError(msg(e))
      return false
    }
  }

  async function openFile() {
    try {
      const r = await invoke('files.open', {})
      if (r.cancelled) return
      const file = { path: r.path ?? '', name: r.name ?? 'script.sql', content: r.content ?? '' }
      if (connections.length === 0) {
        setError('Crie uma conexão antes de abrir um arquivo .sql.')
        return
      }
      setPicker({ purpose: 'open-file', title: `Abrir “${file.name}” — escolha a conexão`, file })
    } catch (e) {
      setError(msg(e))
    }
  }

  function onPick(c: ConnectionInfo) {
    const p = picker
    setPicker(null)
    if (!p) return
    if (p.purpose === 'new-tab') addTab(c)
    else if (p.purpose === 'open-file' && p.file) addTab(c, { text: p.file.content, title: p.file.name, filePath: p.file.path })
    else if (p.purpose === 'assign' && p.tabId) {
      dispatch({ type: 'setConnection', id: p.tabId, connectionId: c.id })
    }
  }

  // ---------- Conexões (painel lateral) ----------
  async function duplicate(c: ConnectionInfo) {
    try {
      const copy = await invoke('connections.duplicate', { id: c.id })
      await reloadConnections()
      setSelectedId(copy.id)
    } catch (e) {
      setError(msg(e))
    }
  }

  async function disconnectConnection(c: ConnectionInfo) {
    try {
      await invoke('connections.disconnect', { id: c.id })
      tabs.filter((t) => t.connectionId === c.id && t.status === 'connected').forEach((t) => dispatch({ type: 'setStatus', id: t.id, status: 'disconnected' }))
    } catch (e) {
      setError(msg(e))
    }
  }

  async function confirmDelete() {
    if (!deleting) return
    try {
      await invoke('connections.disconnect', { id: deleting.id })
      await invoke('connections.delete', { id: deleting.id })
      dispatch({ type: 'detachConnection', connectionId: deleting.id })
      if (selectedId === deleting.id) setSelectedId(null)
      setDeleting(null)
      await reloadConnections()
    } catch (e) {
      setDeleting(null)
      setError(msg(e))
    }
  }

  // ---------- Atalhos globais (fase de captura: antes do Monaco) ----------
  const latest = useRef({ newTab, requestClose, saveTab, openFile, activeTab })
  latest.current = { newTab, requestClose, saveTab, openFile, activeTab }
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!e.ctrlKey || e.altKey) return
      const k = e.key.toLowerCase()
      const l = latest.current
      const run = (fn: () => void) => {
        e.preventDefault()
        e.stopPropagation()
        fn()
      }
      if (k === 't') run(l.newTab)
      else if (k === 'w') run(() => l.activeTab && l.requestClose(l.activeTab))
      else if (k === 'tab') run(() => dispatch({ type: 'cycle', direction: e.shiftKey ? -1 : 1 }))
      else if (k === 's') run(() => l.activeTab && void l.saveTab(l.activeTab, e.shiftKey))
      else if (k === 'o') run(() => void l.openFile())
      else if (k === 'b') run(() => setSidebarOpen((o) => !o))
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [])

  // ---------- Divisores redimensionáveis ----------
  function drag(onMove: (ev: MouseEvent) => void, onEnd: () => void) {
    const move = (ev: MouseEvent) => onMove(ev)
    const up = () => {
      window.removeEventListener('mousemove', move)
      window.removeEventListener('mouseup', up)
      document.body.style.cursor = ''
      onEnd()
    }
    window.addEventListener('mousemove', move)
    window.addEventListener('mouseup', up)
  }

  function startSidebarResize(e: React.MouseEvent) {
    e.preventDefault()
    document.body.style.cursor = 'col-resize'
    let w = sidebarWidth
    drag(
      (ev) => setSidebarWidth((w = Math.min(420, Math.max(180, ev.clientX)))),
      () => storeNumber('sidebarWidth', w),
    )
  }

  function startResultsResize(e: React.MouseEvent) {
    e.preventDefault()
    document.body.style.cursor = 'row-resize'
    const bottom = (e.currentTarget.parentElement as HTMLElement).getBoundingClientRect().bottom
    let h = resultsHeight
    drag(
      (ev) => setResultsHeight((h = Math.min(window.innerHeight - 320, Math.max(100, bottom - ev.clientY)))),
      () => storeNumber('resultsHeight', h),
    )
  }

  const promptTab = tabs.find((t) => t.id === promptTabId) ?? null
  const promptConn = connById(promptTab?.connectionId ?? null)

  return (
    <div className="flex h-full flex-col bg-app text-fg">
      <TitleBar onToggleSidebar={() => setSidebarOpen((o) => !o)} />
      <div className="flex min-h-0 flex-1">
        {sidebarOpen && (
          <>
            <Sidebar
              connections={connections}
              selectedId={selectedId}
              connectedIds={connectedIds}
              width={sidebarWidth}
              onSelect={setSelectedId}
              onNew={() => setEditing({ connection: null })}
              onNewQuery={addTab}
              onEdit={(c) => setEditing({ connection: c })}
              onDuplicate={(c) => void duplicate(c)}
              onDisconnect={(c) => void disconnectConnection(c)}
              onDelete={setDeleting}
            />
            <div role="separator" aria-orientation="vertical" className="-ml-px w-1 shrink-0 cursor-col-resize hover:bg-line" onMouseDown={startSidebarResize} />
          </>
        )}

        <main className="flex min-w-0 flex-1 flex-col">
          <TabBar
            tabs={tabs}
            activeId={activeId}
            connections={connections}
            onActivate={(id) => dispatch({ type: 'activate', id })}
            onClose={(id) => {
              const t = tabs.find((x) => x.id === id)
              if (t) requestClose(t)
            }}
            onRename={(id, title) => dispatch({ type: 'rename', id, title })}
            onNew={newTab}
          />

          {error && (
            <p role="alert" className="flex items-center justify-between bg-red-200 px-4 py-2 text-sm text-red-950">
              {error}
              <button className="icon ml-4" aria-label="Dispensar aviso" onClick={() => setError(null)}>&#xE8BB;</button>
            </p>
          )}

          {activeTab ? (
            <>
              <Toolbar
                tab={activeTab}
                connection={activeConn}
                color={activeColor}
                onSave={() => void saveTab(activeTab)}
                onOpen={() => void openFile()}
                onConnect={() => requestConnect(activeTab)}
                onDisconnect={() => void disconnectTab(activeTab)}
                onPickConnection={() => setPicker({ purpose: 'assign', title: 'Escolha a conexão desta aba', tabId: activeTab.id })}
              />
              {(activeTab.status === 'error' || activeTab.status === 'no-connection') && activeTab.statusMessage && (
                <div role="alert" className="flex items-center gap-4 border-b border-line bg-hover px-4 py-2 text-sm">
                  <p title={activeTab.statusMessage} className="min-w-0 flex-1 truncate">{activeTab.statusMessage}</p>
                  {activeTab.certificateUntrusted && (
                    <label className="flex shrink-0 cursor-pointer items-center gap-2 font-medium">
                      <input type="checkbox" checked={false} onChange={() => void trustCertificateAndReconnect(activeTab)} />
                      Confiar no certificado deste servidor e reconectar
                    </label>
                  )}
                </div>
              )}
              <div className="flex min-h-0 flex-1 flex-col">
                <div className="min-h-0 flex-1 bg-surface">
                  <EditorPane
                    tabId={activeTab.id}
                    initialText={activeTab.text}
                    theme={theme}
                    onChange={(text) => dispatch({ type: 'setText', id: activeTab.id, text })}
                  />
                </div>
                <div role="separator" aria-orientation="horizontal" className="h-1 shrink-0 cursor-row-resize border-t border-line hover:bg-line" onMouseDown={startResultsResize} />
                <div style={{ height: resultsHeight }} className="shrink-0">
                  <ResultsPanel color={activeColor} />
                </div>
              </div>
            </>
          ) : (
            <div className="flex flex-1 flex-col items-center justify-center gap-2 text-muted">
              <p>Nenhuma aba aberta.</p>
              <p className="text-sm">Dê um duplo clique numa conexão ou use Ctrl+T para abrir uma query.</p>
            </div>
          )}
        </main>
      </div>
      <StatusBar tab={activeTab} connection={activeConn} />

      {editing && (
        <ConnectionDialog
          connection={editing.connection}
          defaultColor={nextDefaultColor(connections)}
          onClose={() => setEditing(null)}
          onSaved={async (c) => {
            setEditing(null)
            await reloadConnections()
            setSelectedId(c.id)
          }}
        />
      )}
      {deleting && (
        <ConfirmDialog title="Excluir conexão" confirmLabel="Excluir" danger onConfirm={() => void confirmDelete()} onCancel={() => setDeleting(null)}>
          Excluir a conexão <strong className="text-fg">{deleting.name}</strong>? A senha salva também será removida. As abas dela continuam abertas, sem conexão.
        </ConfirmDialog>
      )}
      {closing && (
        <UnsavedDialog
          title={closing.title}
          onCancel={() => setClosing(null)}
          onDiscard={() => {
            closeNow(closing)
            setClosing(null)
          }}
          onSave={async () => {
            const tab = closing
            setClosing(null)
            if (await saveTab(tab)) closeNow(tab)
          }}
        />
      )}
      {picker && (
        <ConnectionPicker
          connections={connections}
          title={picker.title}
          initialId={activeConn?.id ?? selectedId}
          onPick={onPick}
          onCancel={() => setPicker(null)}
        />
      )}
      {promptTab && promptConn && (
        <PasswordPrompt
          connectionName={promptConn.name}
          error={promptError}
          onCancel={() => setPromptTabId(null)}
          onSubmit={(pwd) => {
            setPromptError(undefined)
            void connectTab(promptTab.id, promptConn.id, pwd)
          }}
        />
      )}
    </div>
  )
}
