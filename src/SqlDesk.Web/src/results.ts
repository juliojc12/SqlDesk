/**
 * Estado dos resultados de uma aba de query: sub-abas "Resultado N", mensagens e andamento da execução.
 * Funções puras; o `rows` de um result set é a única parte mutada no lugar (empurrar lotes de 500 linhas copiando
 * o array inteiro a cada evento seria quadrático). O objeto do result set é sempre substituído, então quem
 * compara por identidade enxerga a mudança.
 */
import type {
  Cell,
  ColumnInfo,
  DocRange,
  ExecuteResponse,
  ExecuteStatus,
  MessageKind,
  QueryMessageEvent,
  QueryResultCompletedEvent,
  QueryResultStartedEvent,
  QueryRowsEvent,
  QueryStartedEvent,
} from './contracts'

export interface ResultSet {
  key: string
  title: string
  columns: ColumnInfo[]
  rows: Cell[][]
  rowCount: number
  truncated: boolean
  done: boolean
  /** Trecho do documento que gerou o resultado. */
  source: DocRange
  /** Texto desse trecho no momento da execução (para "Carregar todas", mesmo que o editor tenha mudado). */
  sourceText: string
}

export interface ResultMessage {
  id: number
  kind: MessageKind
  text: string
  line?: number
}

export const MESSAGES_TAB = 'messages'

export interface TabResults {
  executionId: string | null
  running: boolean
  /** O texto completo enviado na execução em andamento. */
  runText: string
  /** A execução em andamento preserva os resultados anteriores (Ctrl+\). */
  keepPrevious: boolean
  /** Quando a execução em andamento começou (ms), para o contador do indicador de carregamento. */
  startedAt: number | null
  sets: ResultSet[]
  messages: ResultMessage[]
  /** Numeração de "Resultado N". */
  counter: number
  nextMessageId: number
  /** Chave do result set exibido, ou MESSAGES_TAB. */
  active: string
  lastRun: { status: ExecuteStatus; elapsedMs: number; totalRows: number } | null
}

export const emptyResults: TabResults = {
  executionId: null,
  running: false,
  runText: '',
  keepPrevious: false,
  startedAt: null,
  sets: [],
  messages: [],
  counter: 0,
  nextMessageId: 1,
  active: MESSAGES_TAB,
  lastRun: null,
}

export type ResultEvent =
  | { type: 'started'; payload: QueryStartedEvent }
  | { type: 'resultStarted'; payload: QueryResultStartedEvent }
  | { type: 'rows'; payload: QueryRowsEvent }
  | { type: 'resultCompleted'; payload: QueryResultCompletedEvent }
  | { type: 'message'; payload: QueryMessageEvent }

/** Marca o início do pedido de execução. Os resultados antigos só são trocados quando o backend confirma (`started`). */
export function beginRun(prev: TabResults, executionId: string, runText: string, keepPrevious: boolean, now = Date.now()): TabResults {
  return { ...prev, executionId, running: true, runText, keepPrevious, startedAt: now }
}

const setKey = (executionId: string, index: number) => `${executionId}:${index}`

function withSet(state: TabResults, key: string, fn: (s: ResultSet) => ResultSet): TabResults {
  const i = state.sets.findIndex((s) => s.key === key)
  if (i < 0) return state
  const sets = state.sets.slice()
  sets[i] = fn(state.sets[i])
  return { ...state, sets }
}

function addMessage(state: TabResults, kind: MessageKind, text: string, line?: number): TabResults {
  const message: ResultMessage = { id: state.nextMessageId, kind, text, line }
  return { ...state, messages: [...state.messages, message], nextMessageId: state.nextMessageId + 1 }
}

export function applyEvent(state: TabResults, ev: ResultEvent): TabResults {
  // Eventos de uma execução antiga (já substituída ou terminada) são descartados.
  if (ev.payload.executionId !== state.executionId) return state

  switch (ev.type) {
    case 'started':
      return state.keepPrevious ? state : { ...state, sets: [], messages: [], counter: 0, active: MESSAGES_TAB }

    case 'resultStarted': {
      const p = ev.payload
      const counter = state.counter + 1
      const set: ResultSet = {
        key: setKey(p.executionId, p.resultIndex),
        title: `Resultado ${counter}`,
        columns: p.columns,
        rows: [],
        rowCount: 0,
        truncated: false,
        done: false,
        source: p.source,
        sourceText: state.runText.substr(p.source.start, p.source.length),
      }
      // O primeiro resultado da execução vira a sub-aba ativa; os seguintes não roubam o foco.
      const firstOfRun = !state.sets.some((s) => s.key.startsWith(`${p.executionId}:`))
      return { ...state, counter, sets: [...state.sets, set], active: firstOfRun ? set.key : state.active }
    }

    case 'rows': {
      const p = ev.payload
      return withSet(state, setKey(p.executionId, p.resultIndex), (s) => {
        for (const row of p.rows) s.rows.push(row)
        return { ...s, rowCount: s.rows.length }
      })
    }

    case 'resultCompleted': {
      const p = ev.payload
      return withSet(state, setKey(p.executionId, p.resultIndex), (s) => ({ ...s, rowCount: p.rowCount, truncated: p.truncated, done: true }))
    }

    case 'message':
      return addMessage(state, ev.payload.kind, ev.payload.text, ev.payload.line)
  }
}

/** Fim da execução (resposta de `query.execute`). Sem result sets na execução, mostra as mensagens. */
export function finishRun(state: TabResults, executionId: string, response: ExecuteResponse): TabResults {
  if (state.executionId !== executionId) return state
  const producedSets = state.sets.some((s) => s.key.startsWith(`${executionId}:`))
  // Nada foi executado (sem texto, ou a execução espera uma resposta do usuário): preserva resultados e rodapé.
  const untouched = response.status === 'nothing' || response.status === 'needs_confirmation' || response.status === 'advise_transaction'
  const active = untouched || producedSets ? state.active : MESSAGES_TAB
  return {
    ...state,
    running: false,
    active,
    lastRun: untouched ? state.lastRun : { status: response.status, elapsedMs: response.elapsedMs, totalRows: response.totalRows },
  }
}

/** A requisição falhou antes ou durante a execução (por exemplo, aba sem conexão). */
export function failRun(state: TabResults, executionId: string, text: string): TabResults {
  if (state.executionId !== executionId) return state
  return { ...addMessage(state, 'error', text), running: false, active: MESSAGES_TAB }
}

export function formatElapsed(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)} ms`
  if (ms < 60_000) return `${(ms / 1000).toFixed(2).replace('.', ',')} s`
  return `${Math.floor(ms / 60_000)} min ${Math.floor((ms % 60_000) / 1000)} s`
}
