import { useSyncExternalStore } from 'react'
import { buildIndex, type MetaIndex, type MetadataDto } from './metadataIndex'
import type { ProviderId } from './providers'

export interface MetaState {
  index: MetaIndex | null
  loading: boolean
  columnsLoaded: boolean
  error?: string
}

const empty: MetaState = { index: null, loading: false, columnsLoaded: false }
const states = new Map<string, MetaState>()
const listeners = new Set<() => void>()

/** Banco atual de cada conexão (`settings.database`), que o índice usa para desempatar nomes sem schema no MySQL. */
const connectionDatabases = new Map<string, string>()

/** Conexão de cada aba (o provider do Monaco só enxerga o modelo, que tem o id da aba no caminho). */
const tabConnections = new Map<string, string | null>()

export const getMeta = (connectionId: string | null | undefined): MetaState => (connectionId ? states.get(connectionId) ?? empty : empty)

export function patchMeta(connectionId: string, patch: Partial<MetaState>) {
  states.set(connectionId, { ...getMeta(connectionId), ...patch })
  listeners.forEach((l) => l())
}

/** Aplica a resposta de `metadata.get`. */
export function applyMetadata(connectionId: string, dto: MetadataDto) {
  patchMeta(connectionId, {
    index: dto.loaded ? buildIndex(dto, connectionDatabases.get(connectionId)) : getMeta(connectionId).index,
    // Cache completo nunca é "carregando": protege a interface de um estado transitório do backend.
    loading: dto.loading && !dto.columnsLoaded,
    columnsLoaded: dto.columnsLoaded,
    error: undefined,
  })
}

/** Informa o banco atual da conexão; vale também para o índice que já foi montado. */
export function setConnectionDatabase(connectionId: string, database: string | undefined) {
  if (database) connectionDatabases.set(connectionId, database)
  else connectionDatabases.delete(connectionId)
  const index = states.get(connectionId)?.index
  if (index) index.defaultSchema = database || undefined
}

export function useMeta(connectionId: string | null | undefined): MetaState {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l)
      return () => listeners.delete(l)
    },
    () => getMeta(connectionId),
  )
}

export function setTabConnection(tabId: string, connectionId: string | null) {
  tabConnections.set(tabId, connectionId)
}

export const connectionOfTab = (tabId: string): string | null => tabConnections.get(tabId) ?? null

/** Banco (provedor) de cada conexão, para o autocomplete e o formatador escolherem o dialeto da aba. */
const connectionProviders = new Map<string, ProviderId>()

export function setConnectionProvider(connectionId: string, provider: ProviderId) {
  connectionProviders.set(connectionId, provider)
}

/** Provedor da conexão da aba; aba sem conexão ou conexão desconhecida vale SQL Server (o comportamento de sempre). */
export function providerOfTab(tabId: string): ProviderId {
  const connectionId = connectionOfTab(tabId)
  return (connectionId && connectionProviders.get(connectionId)) || 'sqlserver'
}
