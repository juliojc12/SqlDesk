/** Estado das abas de query: reducer puro, persistência e regras de navegação. */

export type ConnStatus = 'idle' | 'disconnected' | 'connecting' | 'connected' | 'error' | 'needs-password' | 'no-connection'

export interface Tab {
  id: string
  title: string
  connectionId: string | null
  text: string
  /** Texto da última gravação em arquivo (ou o inicial). Diferente de `text` = alterações não salvas. */
  savedText: string
  filePath: string | null
  status: ConnStatus
  statusMessage?: string
  serverVersion?: string
  /** A última falha foi de certificado não confiável: a aba oferece "confiar e reconectar". */
  certificateUntrusted?: boolean
}

export interface TabsState {
  tabs: Tab[]
  activeId: string | null
  /** Contador para nomear "Query N". Nunca diminui, para não repetir nomes. */
  counter: number
}

export const initialTabsState: TabsState = { tabs: [], activeId: null, counter: 0 }

export type TabsAction =
  | { type: 'add'; id: string; connectionId: string | null; text?: string; title?: string; filePath?: string | null }
  | { type: 'close'; id: string }
  | { type: 'activate'; id: string }
  | { type: 'cycle'; direction: 1 | -1 }
  | { type: 'rename'; id: string; title: string }
  | { type: 'move'; id: string; toIndex: number }
  | { type: 'setText'; id: string; text: string }
  | { type: 'setStatus'; id: string; status: ConnStatus; message?: string; serverVersion?: string; certificateUntrusted?: boolean }
  | { type: 'setConnection'; id: string; connectionId: string | null }
  | { type: 'detachConnection'; connectionId: string }
  | { type: 'saved'; id: string; filePath: string; title: string }
  | { type: 'restore'; state: TabsState }

export const isDirty = (t: Tab) => t.text !== t.savedText

/**
 * A última tentativa de conectar falhou (ex.: VPN desligada): Executar tenta conectar de novo antes de rodar.
 * Aba desconectada (pelo usuário ou por queda da conexão) não entra: reconectar ali é sempre uma ação explícita.
 */
export const retriesOnRun = (t: Tab) => t.status === 'error' && t.connectionId !== null

export function tabsReducer(state: TabsState, a: TabsAction): TabsState {
  const patch = (id: string, p: Partial<Tab>): TabsState => ({
    ...state,
    tabs: state.tabs.map((t) => (t.id === id ? { ...t, ...p } : t)),
  })

  switch (a.type) {
    case 'add': {
      const counter = a.title ? state.counter : state.counter + 1
      const text = a.text ?? ''
      const tab: Tab = {
        id: a.id,
        title: a.title ?? `Query ${counter}`,
        connectionId: a.connectionId,
        text,
        savedText: text,
        filePath: a.filePath ?? null,
        status: a.connectionId ? 'idle' : 'no-connection',
      }
      return { tabs: [...state.tabs, tab], activeId: tab.id, counter }
    }
    case 'close': {
      const i = state.tabs.findIndex((t) => t.id === a.id)
      if (i < 0) return state
      const tabs = state.tabs.filter((t) => t.id !== a.id)
      // Fechar a aba ativa ativa a vizinha (a da direita; se era a última, a da esquerda).
      const activeId = state.activeId === a.id ? (tabs[Math.min(i, tabs.length - 1)]?.id ?? null) : state.activeId
      return { ...state, tabs, activeId }
    }
    case 'activate':
      return state.tabs.some((t) => t.id === a.id) ? { ...state, activeId: a.id } : state
    case 'cycle': {
      if (state.tabs.length === 0) return state
      const i = state.tabs.findIndex((t) => t.id === state.activeId)
      const next = (i + a.direction + state.tabs.length) % state.tabs.length
      return { ...state, activeId: state.tabs[next].id }
    }
    case 'move': {
      // Reordenar só muda a posição: a aba ativa, o texto e as conexões continuam como estão.
      const from = state.tabs.findIndex((t) => t.id === a.id)
      const to = Math.max(0, Math.min(a.toIndex, state.tabs.length - 1))
      if (from < 0 || from === to) return state
      const tabs = state.tabs.slice()
      const [t] = tabs.splice(from, 1)
      tabs.splice(to, 0, t)
      return { ...state, tabs }
    }
    case 'rename': {
      const title = a.title.trim()
      return title ? patch(a.id, { title }) : state
    }
    case 'setText':
      return patch(a.id, { text: a.text })
    case 'setStatus':
      return patch(a.id, { status: a.status, statusMessage: a.message, serverVersion: a.serverVersion, certificateUntrusted: a.certificateUntrusted })
    case 'setConnection':
      return patch(a.id, { connectionId: a.connectionId, status: a.connectionId ? 'idle' : 'no-connection', statusMessage: undefined })
    case 'detachConnection':
      return {
        ...state,
        tabs: state.tabs.map((t) =>
          t.connectionId === a.connectionId
            ? { ...t, connectionId: null, status: 'no-connection' as const, statusMessage: 'A conexão foi excluída.' }
            : t,
        ),
      }
    case 'saved':
      return patch(a.id, { filePath: a.filePath, title: a.title, savedText: state.tabs.find((t) => t.id === a.id)?.text ?? '' })
    case 'restore':
      return a.state
  }
}

// ---- Persistência ----

interface PersistedTab {
  id: string
  title: string
  connectionId: string | null
  text: string
  savedText: string
  filePath: string | null
}

interface Persisted {
  version: 1
  counter: number
  activeId: string | null
  tabs: PersistedTab[]
}

export function serialize(state: TabsState): string {
  const p: Persisted = {
    version: 1,
    counter: state.counter,
    activeId: state.activeId,
    tabs: state.tabs.map(({ id, title, connectionId, text, savedText, filePath }) => ({ id, title, connectionId, text, savedText, filePath })),
  }
  return JSON.stringify(p)
}

/**
 * Restaura as abas. Nunca conecta: todas voltam como `idle` (ou `no-connection` se a conexão não existe mais).
 * Devolve o estado inicial vazio se o JSON for ilegível.
 */
export function deserialize(json: string | null | undefined, knownConnectionIds: ReadonlySet<string>): TabsState {
  if (!json) return initialTabsState
  try {
    const p = JSON.parse(json) as Partial<Persisted>
    if (p.version !== 1 || !Array.isArray(p.tabs)) return initialTabsState
    const tabs: Tab[] = p.tabs
      .filter((t): t is PersistedTab => typeof t?.id === 'string' && typeof t.text === 'string')
      .map((t) => {
        const known = t.connectionId !== null && knownConnectionIds.has(t.connectionId)
        return {
          id: t.id,
          title: typeof t.title === 'string' && t.title ? t.title : 'Query',
          connectionId: known ? t.connectionId : null,
          text: t.text,
          savedText: typeof t.savedText === 'string' ? t.savedText : t.text,
          filePath: typeof t.filePath === 'string' ? t.filePath : null,
          status: known ? ('idle' as const) : ('no-connection' as const),
          statusMessage: !known && t.connectionId ? 'A conexão desta aba não existe mais.' : undefined,
        }
      })
    const activeId = tabs.some((t) => t.id === p.activeId) ? (p.activeId as string) : (tabs[0]?.id ?? null)
    return { tabs, activeId, counter: typeof p.counter === 'number' ? p.counter : tabs.length }
  } catch {
    return initialTabsState
  }
}
