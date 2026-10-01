import { useSyncExternalStore } from 'react'
import { on } from './bridge'
import type {
  QueryMessageEvent,
  QueryResultCompletedEvent,
  QueryResultStartedEvent,
  QueryRowsEvent,
  QueryStartedEvent,
} from './contracts'
import { applyEvent, emptyResults, type ResultEvent, type TabResults } from './results'

/**
 * Armazém externo (fora do React) dos resultados por aba. Eventos chegam em rajada durante uma execução; aplicá-los
 * aqui evita reprocessar o estado da árvore de componentes e permite empurrar linhas no lugar (ver results.ts).
 */
const states = new Map<string, TabResults>()
const listeners = new Set<() => void>()

export const getResults = (tabId: string): TabResults => states.get(tabId) ?? emptyResults

export function updateResults(tabId: string, fn: (s: TabResults) => TabResults) {
  const next = fn(getResults(tabId))
  if (next === getResults(tabId)) return
  states.set(tabId, next)
  listeners.forEach((l) => l())
}

export function clearResults(tabId: string) {
  if (states.delete(tabId)) listeners.forEach((l) => l())
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function useTabResults(tabId: string | null): TabResults {
  return useSyncExternalStore(subscribe, () => (tabId ? getResults(tabId) : emptyResults))
}

/** Liga os eventos `query.*` da ponte ao armazém. Devolve a função que desliga. */
export function listenToQueryEvents(): () => void {
  const feed = (type: ResultEvent['type'], payload: { tabId: string }) =>
    updateResults(payload.tabId, (s) => applyEvent(s, { type, payload } as ResultEvent))

  const offs = [
    on<QueryStartedEvent>('query.started', (p) => feed('started', p)),
    on<QueryResultStartedEvent>('query.resultStarted', (p) => feed('resultStarted', p)),
    on<QueryRowsEvent>('query.rows', (p) => feed('rows', p)),
    on<QueryResultCompletedEvent>('query.resultCompleted', (p) => feed('resultCompleted', p)),
    on<QueryMessageEvent>('query.message', (p) => feed('message', p)),
  ]
  return () => offs.forEach((off) => off())
}
