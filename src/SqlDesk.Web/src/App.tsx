import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from 'react'
import { BridgeCallError, invoke, on } from './bridge'
import { ConfirmDialog } from './components/ConfirmDialog'
import { ConnectionDialog, nextDefaultColor } from './components/ConnectionDialog'
import { EditorPane, modelPath } from './components/EditorPane'
import { ResultsPanel } from './components/ResultsPanel'
import { Sidebar } from './components/Sidebar'
import { StatusBar } from './components/StatusBar'
import { AppCloseDialog, DangerDialog, GuardDialog, TranAdviceBar, TranCloseDialog } from './components/GuardDialogs'
import { ConnectionPicker, PasswordPrompt, UnsavedDialog } from './components/TabDialogs'
import { TabBar } from './components/TabBar'
import { TitleBar } from './components/TitleBar'
import { Toolbar } from './components/Toolbar'
import { generateAlias } from './alias'
import { NEUTRAL_COLOR } from './colors'
import { registerCompletion } from './completion'
import type {
  AppCloseRequestedEvent, ConnectionInfo, DocRange, GuardExpiredEvent, GuardInfo, MetadataUpdatedEvent, QueryStartedEvent, TabConnectionLostEvent,
  TabTransactionEvent,
} from './contracts'
import { highlightRange, revealLine, snapshotOf, wrapInTransaction, type EditorSnapshot } from './editorActions'
import { quoteIfNeeded } from './suggest'
import { applyMetadata, getMeta, patchMeta, setTabConnection, useMeta } from './metadataStore'
import type { MetaObject } from './metadataIndex'
import { monaco } from './monacoSetup'
import { getAutoAlias, setAutoAlias } from './settings'
import { beginRun, failRun, finishRun, type ResultSet } from './results'
import { clearResults, getResults, listenToQueryEvents, updateResults, useTabResults } from './resultsStore'
import { deserialize, initialTabsState, isDirty, serialize, tabsReducer, type Tab } from './tabsState'
import { useTheme } from './useTheme'

const msg = (e: unknown) => (e instanceof BridgeCallError ? e.detail.message : String(e))

async function loadMetadata(connectionId: string) {
  try {
    applyMetadata(connectionId, await invoke('metadata.get', { connectionId }))
  } catch (e) {
    patchMeta(connectionId, { loading: false, error: msg(e) })
  }
}

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

interface RunOpts {
  newSubTab?: boolean
  snapshot?: EditorSnapshot
  noRowLimit?: boolean
  confirmDangerous?: boolean
  skipTranAdvice?: boolean
  /** A chamada vem de um diálogo que acabou de fechar (o estado dele ainda não saiu do ref). */
  fromDialog?: boolean
}
type RunMode = 'current' | 'script'

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
  const [notice, setNotice] = useState<string | null>(null)
  const [tranCounts, setTranCounts] = useState<Record<string, number>>({})
  const [danger, setDanger] = useState<{ tabId: string; mode: RunMode; opts: RunOpts; blocked: { line: number; description: string }[] } | null>(null)
  const [guardDlg, setGuardDlg] = useState<{ tabId: string; guard: GuardInfo } | null>(null)
  const [guardBusy, setGuardBusy] = useState(false)
  const [advice, setAdvice] = useState<{ tabId: string; mode: RunMode; opts: RunOpts; range: DocRange } | null>(null)
  const [noAdvice, setNoAdvice] = useState<ReadonlySet<string>>(new Set())
  const [closingTran, setClosingTran] = useState<Tab | null>(null)
  const [tranBusy, setTranBusy] = useState(false)
  const [appClose, setAppClose] = useState<{ tabId: string; count: number }[] | null>(null)
  const [autoAlias, setAutoAliasState] = useState(getAutoAlias)
  const [confirmDisconnect, setConfirmDisconnect] = useState<{ message: string; run: () => Promise<void> } | null>(null)
  const [sidebarOpen, setSidebarOpen] = useState(true)
  const [sidebarWidth, setSidebarWidth] = useState(() => loadNumber('sidebarWidth', 260))
  const [resultsHeight, setResultsHeight] = useState(() => loadNumber('resultsHeight', 260))

  const { tabs, activeId } = tabsState
  const activeTab = tabs.find((t) => t.id === activeId) ?? null
  const connById = useCallback((id: string | null) => connections.find((c) => c.id === id) ?? null, [connections])
  const activeConn = connById(activeTab?.connectionId ?? null)
  const activeColor = activeConn?.color ?? NEUTRAL_COLOR
  const results = useTabResults(activeId)
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

  // ---------- Eventos de execução (resultados, mensagens, destaque do statement) ----------
  useEffect(() => {
    const offResults = listenToQueryEvents()
    const offHighlight = on<QueryStartedEvent>('query.started', (p) => {
      if (p.range) highlightRange(p.tabId, p.range)
    })
    const offTran = on<TabTransactionEvent>('tab.transaction', (p) =>
      setTranCounts((m) => (m[p.tabId] === p.count ? m : { ...m, [p.tabId]: p.count })),
    )
    const offLost = on<TabConnectionLostEvent>('tab.connectionLost', (p) => {
      dispatch({ type: 'setStatus', id: p.tabId, status: 'disconnected', message: 'A conexão com o servidor caiu.' })
      setGuardDlg((g) => (g?.tabId === p.tabId ? null : g))
      if (p.hadTransaction) setError('A conexão caiu e o servidor desfez a transação aberta (rollback): as alterações não confirmadas foram perdidas.')
      else setNotice('A conexão com o servidor caiu.')
    })
    // Sem resposta em 120 s o backend já fez o rollback; só resta fechar o diálogo e avisar.
    const offExpired = on<GuardExpiredEvent>('query.guardExpired', (p) => {
      setGuardDlg((g) => (g?.tabId === p.tabId ? null : g))
      setNotice(p.message)
    })
    const offClose = on<AppCloseRequestedEvent>('app.closeRequested', (p) => setAppClose(p.tabs))
    const offMeta = on<MetadataUpdatedEvent>('metadata.updated', (p) => {
      if (p.phase === 'error') patchMeta(p.connectionId, { loading: false, error: p.message ?? 'Não foi possível carregar os metadados.' })
      else void loadMetadata(p.connectionId)
    })
    return () => {
      offMeta()
      offResults()
      offHighlight()
      offTran()
      offLost()
      offExpired()
      offClose()
    }
  }, [])

  useEffect(() => {
    if (!notice) return
    const h = window.setTimeout(() => setNotice(null), 5000)
    return () => window.clearTimeout(h)
  }, [notice])

  // ---------- Metadados (autocomplete e árvore de objetos) ----------
  useEffect(() => {
    registerCompletion()
  }, [])

  // O provider do Monaco só enxerga o modelo da aba: aqui se mantém a conexão de cada uma.
  useEffect(() => {
    tabs.forEach((t) => setTabConnection(t.id, t.connectionId))
  }, [tabs])

  /** Dispara o carregamento em segundo plano (sem bloquear a interface). Sem `force`, não recarrega cache completo. */
  async function ensureMetadata(connectionId: string, force = false) {
    const current = getMeta(connectionId)
    if (!force && (current.loading || current.columnsLoaded)) return
    patchMeta(connectionId, { loading: true, error: undefined })
    try {
      const r = await invoke('metadata.refresh', { connectionId, force })
      if (!r.started && !r.loading) await loadMetadata(connectionId) // o backend já tinha o cache (o frontend foi recarregado)
    } catch (e) {
      patchMeta(connectionId, { loading: false, error: msg(e) })
    }
  }

  /** Duplo clique na árvore: abre uma nova aba com `SELECT TOP 100 * FROM schema.Tabela alias` (ou EXEC, para procedure). */
  function openObject(conn: ConnectionInfo, o: MetaObject) {
    const name = `${quoteIfNeeded(o.schema)}.${quoteIfNeeded(o.name)}`
    const text = o.type === 'procedure' ? `EXEC ${name}` : `SELECT TOP 100 * FROM ${name} ${generateAlias(o.name)}`
    addTab(conn, { text, title: o.name })
  }

  function toggleAutoAlias() {
    setAutoAliasState((on) => {
      setAutoAlias(!on)
      return !on
    })
  }

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
      void ensureMetadata(connectionId)
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

  function clearTran(tabId: string) {
    setTranCounts((m) => (m[tabId] ? { ...m, [tabId]: 0 } : m))
  }

  async function disconnectNow(tab: Tab) {
    try {
      await invoke('tabs.disconnect', { tabId: tab.id })
      clearTran(tab.id)
      dispatch({ type: 'setStatus', id: tab.id, status: 'disconnected' })
    } catch (e) {
      setError(msg(e))
    }
  }

  /** Desconectar com transação aberta desfaz tudo (o servidor faz rollback): pede confirmação. */
  function disconnectTab(tab: Tab) {
    if ((tranCounts[tab.id] ?? 0) > 0) {
      setConfirmDisconnect({
        message: `A aba “${tab.title}” tem uma transação aberta. Desconectar desfaz (rollback) as alterações não confirmadas.`,
        run: () => disconnectNow(tab),
      })
    } else void disconnectNow(tab)
  }

  // ---------- Execução ----------
  /**
   * Pede ao backend para executar. O backend decide o que rodar (seleção, statement sob o cursor ou script) e
   * aplica as travas; aqui só se envia texto, cursor e seleção. Uma aba executa um comando por vez.
   */
  async function execute(mode: RunMode, opts: RunOpts = {}) {
    const l = latest.current
    const tab = l.activeTab
    if (!tab || getResults(tab.id).running || (l.modalOpen && !opts.fromDialog)) return
    if (tab.status !== 'connected') {
      setNotice('Conecte a aba antes de executar.')
      return
    }
    setAdvice(null)
    const snap = opts.snapshot ?? snapshotOf(tab.id) ?? { text: tab.text, cursor: 0, selectionStart: 0, selectionEnd: 0 }
    const executionId = crypto.randomUUID()
    updateResults(tab.id, (s) => beginRun(s, executionId, snap.text, opts.newSubTab ?? false))
    try {
      const r = await invoke('query.execute', {
        tabId: tab.id, executionId, ...snap, mode,
        noRowLimit: opts.noRowLimit ?? false,
        confirmDangerous: opts.confirmDangerous ?? false,
        skipTranAdvice: opts.skipTranAdvice ?? l.noAdvice.has(tab.id),
      })
      updateResults(tab.id, (s) => finishRun(s, executionId, r))
      // Em caso de nova tentativa (confirmações), reenvia exatamente o mesmo texto, cursor e seleção.
      const again: RunOpts = { ...opts, snapshot: snap }
      switch (r.status) {
        case 'nothing':
        case 'refused':
          if (r.message) setNotice(r.message)
          break
        case 'needs_confirmation':
          setDanger({ tabId: tab.id, mode, opts: again, blocked: r.blocked ?? [] })
          break
        case 'advise_transaction':
          if (r.range) setAdvice({ tabId: tab.id, mode, opts: again, range: r.range })
          break
        case 'pending_decision':
          if (r.guard) setGuardDlg({ tabId: tab.id, guard: r.guard })
          break
        case 'tran_lost':
          setNotice('O próprio script encerrou a transação (COMMIT ou ROLLBACK): as alterações não podem mais ser desfeitas por aqui.')
          break
      }
    } catch (e) {
      updateResults(tab.id, (s) => failRun(s, executionId, msg(e)))
    }
  }

  async function resolveGuard(commit: boolean) {
    const g = guardDlg
    if (!g) return
    setGuardBusy(true)
    try {
      await invoke('query.guard.resolve', { tabId: g.tabId, guardId: g.guard.guardId, commit })
      setGuardDlg(null)
    } catch (e) {
      if (e instanceof BridgeCallError && e.detail.code === 'guard_expired') {
        setGuardDlg(null)
        setNotice(e.detail.message)
      } else setError(msg(e))
    } finally {
      setGuardBusy(false)
    }
  }

  async function tranAction(kind: 'begin' | 'commit' | 'rollback', tab: Tab): Promise<boolean> {
    setTranBusy(true)
    try {
      const r = await invoke(kind === 'begin' ? 'tran.begin' : kind === 'commit' ? 'tran.commit' : 'tran.rollback', { tabId: tab.id })
      setTranCounts((m) => ({ ...m, [tab.id]: r.tranCount }))
      return true
    } catch (e) {
      setError(msg(e))
      return false
    } finally {
      setTranBusy(false)
    }
  }

  function stop() {
    const tab = latest.current.activeTab
    if (tab && getResults(tab.id).running) void invoke('query.cancel', { tabId: tab.id }).catch((e) => setError(msg(e)))
  }

  /** "Carregar todas": reexecuta o trecho que gerou o resultado, sem o limite de linhas. */
  function loadAll(set: ResultSet) {
    void execute('current', {
      noRowLimit: true,
      snapshot: { text: set.sourceText, cursor: 0, selectionStart: 0, selectionEnd: set.sourceText.length },
    })
  }

  function activateResult(key: string) {
    if (!activeTab) return
    updateResults(activeTab.id, (s) => ({ ...s, active: key }))
    // Clicar numa sub-aba de resultado destaca no editor o trecho que a gerou.
    const set = getResults(activeTab.id).sets.find((s) => s.key === key)
    if (set) highlightRange(activeTab.id, set.source)
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
    clearResults(tab.id)
    clearTran(tab.id)
    setAdvice((a) => (a?.tabId === tab.id ? null : a))
    void invoke('tabs.disconnect', { tabId: tab.id }).catch((e) => setError(msg(e)))
    monaco.editor.getModel(monaco.Uri.parse(modelPath(tab.id)))?.dispose()
  }

  function closeAfterTran(tab: Tab) {
    if (isDirty(tab)) setClosing(tab)
    else closeNow(tab)
  }

  function requestClose(tab: Tab) {
    // Fechar uma aba com transação aberta pede a decisão (Commit / Rollback / Cancelar) antes de qualquer outra pergunta.
    if ((tranCounts[tab.id] ?? 0) > 0) setClosingTran(tab)
    else closeAfterTran(tab)
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

  async function disconnectConnectionNow(c: ConnectionInfo) {
    try {
      await invoke('connections.disconnect', { id: c.id })
      tabs
        .filter((t) => t.connectionId === c.id && t.status === 'connected')
        .forEach((t) => {
          clearTran(t.id)
          dispatch({ type: 'setStatus', id: t.id, status: 'disconnected' })
        })
    } catch (e) {
      setError(msg(e))
    }
  }

  function disconnectConnection(c: ConnectionInfo) {
    const open = tabs.filter((t) => t.connectionId === c.id && (tranCounts[t.id] ?? 0) > 0)
    if (open.length > 0) {
      setConfirmDisconnect({
        message: `${open.length === 1 ? 'Uma aba desta conexão tem' : `${open.length} abas desta conexão têm`} transação aberta. Desconectar desfaz (rollback) as alterações não confirmadas.`,
        run: () => disconnectConnectionNow(c),
      })
    } else void disconnectConnectionNow(c)
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
  const modalOpen = !!(danger || guardDlg || closingTran || appClose || confirmDisconnect)
  const latest = useRef({ newTab, requestClose, saveTab, openFile, activeTab, execute, stop, modalOpen, noAdvice })
  latest.current = { newTab, requestClose, saveTab, openFile, activeTab, execute, stop, modalOpen, noAdvice }
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const l = latest.current
      const run = (fn: () => void) => {
        e.preventDefault()
        e.stopPropagation()
        fn()
      }
      // Execução (como no DBeaver): F5 roda o script; Esc cancela só se houver execução em andamento.
      if (e.key === 'F5' && !e.ctrlKey && !e.altKey) return run(() => void l.execute('script'))
      if (e.key === 'Escape' && l.activeTab && getResults(l.activeTab.id).running) return run(l.stop)

      if (!e.ctrlKey || e.altKey) return
      const k = e.key.toLowerCase()
      if (k === 'enter') return run(() => void l.execute('current'))
      if (e.key === '\\' || e.code === 'Backslash' || e.code === 'IntlBackslash') return run(() => void l.execute('current', { newSubTab: true }))
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

  const activeMeta = useMeta(activeConn?.id)
  const tranTabs = useMemo(
    () => new Set(tabs.filter((t) => (tranCounts[t.id] ?? 0) > 0).map((t) => t.id)),
    [tabs, tranCounts],
  )
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
              onExpand={(c) => void ensureMetadata(c.id)}
              onRefreshMetadata={(c) => void ensureMetadata(c.id, true)}
              onOpenObject={openObject}
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
            tranTabs={tranTabs}
          />

          {error && (
            <p role="alert" className="flex items-center justify-between bg-red-200 px-4 py-2 text-sm text-red-950">
              {error}
              <button className="icon ml-4" aria-label="Dispensar aviso" onClick={() => setError(null)}>&#xE8BB;</button>
            </p>
          )}

          {notice && (
            <p role="status" className="flex items-center justify-between border-b border-line bg-hover px-4 py-2 text-sm">
              {notice}
              <button className="icon ml-4" aria-label="Dispensar aviso" onClick={() => setNotice(null)}>&#xE8BB;</button>
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
                running={results.running}
                onRun={() => void execute('current')}
                onRunScript={() => void execute('script')}
                onStop={stop}
                tranCount={tranCounts[activeTab.id] ?? 0}
                onBeginTran={() => void tranAction('begin', activeTab)}
                autoAlias={autoAlias}
                onToggleAlias={toggleAutoAlias}
                metaLoading={activeMeta.loading}
                onRefreshMetadata={() => activeTab.connectionId && void ensureMetadata(activeTab.connectionId, true)}
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
                {advice && advice.tabId === activeTab.id && (
                  <TranAdviceBar
                    onWrap={() => {
                      wrapInTransaction(advice.tabId, advice.range)
                      setAdvice(null)
                    }}
                    onRunAnyway={() => void execute(advice.mode, { ...advice.opts, skipTranAdvice: true })}
                    onNeverAsk={() => {
                      setNoAdvice((cur) => new Set(cur).add(advice.tabId))
                      void execute(advice.mode, { ...advice.opts, skipTranAdvice: true })
                    }}
                  />
                )}
                <div className="min-h-0 flex-1 bg-surface">
                  <EditorPane
                    tabId={activeTab.id}
                    initialText={activeTab.text}
                    theme={theme}
                    onChange={(text) => {
                      dispatch({ type: 'setText', id: activeTab.id, text })
                      // O texto mudou: a recomendação (que guarda o trecho antigo) deixa de valer.
                      setAdvice((a) => (a?.tabId === activeTab.id ? null : a))
                    }}
                  />
                </div>
                <div role="separator" aria-orientation="horizontal" className="h-1 shrink-0 cursor-row-resize border-t border-line hover:bg-line" onMouseDown={startResultsResize} />
                <div style={{ height: resultsHeight }} className="shrink-0">
                  <ResultsPanel
                    results={results}
                    color={activeColor}
                    onActivate={activateResult}
                    onJumpToLine={(line) => revealLine(activeTab.id, line)}
                    onLoadAll={loadAll}
                    canLoadAll={activeTab.status === 'connected' && !results.running}
                  />
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
      <StatusBar
        tab={activeTab}
        connection={activeConn}
        results={results}
        openTranTabs={tranTabs.size}
        activeTran={!!activeTab && tranTabs.has(activeTab.id)}
        tranBusy={tranBusy || results.running}
        onCommit={() => activeTab && void tranAction('commit', activeTab)}
        onRollback={() => activeTab && void tranAction('rollback', activeTab)}
      />

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
      {danger && (
        <DangerDialog
          blocked={danger.blocked}
          onCancel={() => setDanger(null)}
          onContinue={() => {
            const d = danger
            setDanger(null)
            void execute(d.mode, { ...d.opts, confirmDangerous: true, fromDialog: true })
          }}
        />
      )}
      {guardDlg && (
        <GuardDialog
          guard={guardDlg.guard}
          busy={guardBusy}
          onCommit={() => void resolveGuard(true)}
          onRollback={() => void resolveGuard(false)}
        />
      )}
      {closingTran && (
        <TranCloseDialog
          title={closingTran.title}
          busy={tranBusy}
          onCancel={() => setClosingTran(null)}
          onCommit={async () => {
            const t = closingTran
            if (await tranAction('commit', t)) {
              setClosingTran(null)
              closeAfterTran(t)
            }
          }}
          onRollback={async () => {
            const t = closingTran
            if (await tranAction('rollback', t)) {
              setClosingTran(null)
              closeAfterTran(t)
            }
          }}
        />
      )}
      {confirmDisconnect && (
        <ConfirmDialog
          title="Desconectar"
          confirmLabel="Desconectar e desfazer"
          danger
          onCancel={() => setConfirmDisconnect(null)}
          onConfirm={() => {
            const c = confirmDisconnect
            setConfirmDisconnect(null)
            void c.run()
          }}
        >
          {confirmDisconnect.message}
        </ConfirmDialog>
      )}
      {appClose && (
        <AppCloseDialog
          tabs={appClose.map((t) => ({ ...t, title: tabs.find((x) => x.id === t.tabId)?.title ?? 'Aba' }))}
          onCancel={() => setAppClose(null)}
          onDecide={async (tabId, commit) => {
            const r = await invoke(commit ? 'tran.commit' : 'tran.rollback', { tabId })
            setTranCounts((m) => ({ ...m, [tabId]: r.tranCount }))
          }}
          onDone={() => void invoke('window.forceClose', {})}
        />
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
