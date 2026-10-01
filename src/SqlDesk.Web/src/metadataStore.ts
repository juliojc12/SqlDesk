import { useSyncExternalStore } from 'react'
import { buildIndex, type MetaIndex, type MetadataDto } from './metadataIndex'

export interface MetaState {
  index: MetaIndex | null
  loading: boolean
  columnsLoaded: boolean
  error?: string
}

const empty: MetaState = { index: null, loading: false, columnsLoaded: false }
const states = new Map<string, MetaState>()
const listeners = new Set<() => void>()

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
    index: dto.loaded ? buildIndex(dto) : getMeta(connectionId).index,
    // Cache completo nunca é "carregando": protege a interface de um estado transitório do backend.
    loading: dto.loading && !dto.columnsLoaded,
    columnsLoaded: dto.columnsLoaded,
    error: undefined,
  })
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
