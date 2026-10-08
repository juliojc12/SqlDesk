// Contratos da ponte JS/C#. Espelham os records em SqlDesk.Host/Bridge/Contracts.cs.
import type { ProviderId } from './providers'

export interface Envelope<T = unknown> {
  id: string | null
  type: string
  payload: T
}

export interface BridgeError {
  code: string
  message: string
  line?: number
}

export interface ErrorPayload {
  error: BridgeError
}

export interface ConnectionSettings {
  server: string
  database: string
  user: string
  connectTimeout: number
  commandTimeout: number
  encrypt: boolean
  trustServerCertificate: boolean
  advanced: Record<string, string>
  /** Banco da conexão; ausente em conexões salvas antes do MySQL (vale SQL Server, ver providerOf). */
  provider?: ProviderId
}

/** A senha nunca vem do backend: só o indicador hasPassword. */
export interface ConnectionInfo {
  id: string
  name: string
  color: string
  settings: ConnectionSettings
  hasPassword: boolean
}

export interface SaveConnectionRequest {
  id?: string | null
  name: string
  color: string
  settings: ConnectionSettings
  password?: string | null
}

export interface TestConnectionResult {
  ok: boolean
  serverVersion?: string
  errorMessage?: string
  /** A falha foi de confiança no certificado do servidor: o usuário pode optar por confiar nele. */
  certificateUntrusted?: boolean
}

// ---- Execução ----
export type ColumnKind = 'number' | 'text' | 'date' | 'bool' | 'binary'

export interface ColumnInfo {
  name: string
  kind: ColumnKind
  typeName: string
}

/** Valor de célula: decimal e bigint chegam como texto (o JS perderia precisão). */
export type Cell = string | number | boolean | null

export interface DocRange {
  start: number
  length: number
}

export type MessageKind = 'info' | 'rows' | 'error' | 'timing'

export interface ExecuteRequest {
  tabId: string
  executionId: string
  text: string
  cursor: number
  selectionStart: number
  selectionEnd: number
  /** current = seleção ou statement sob o cursor; script = documento inteiro. */
  mode: 'current' | 'script'
  noRowLimit: boolean
  /** Limite de linhas da grade (configuração do usuário); o backend o ajusta à faixa permitida. */
  maxRows?: number
  /** Resposta à primeira confirmação: executa dentro de uma transação, com a segunda confirmação depois. */
  confirmDangerous?: boolean
  /** O usuário escolheu "Executar assim mesmo" (ou "Não perguntar nesta aba") na barra de recomendação. */
  skipTranAdvice?: boolean
}

export type DangerKind = 'updateWithoutWhere' | 'deleteWithoutWhere' | 'truncateTable' | 'drop' | 'dropColumn' | 'alterTable' | 'unanalyzable'

export interface GuardChange {
  kind: DangerKind
  target?: string
  /** Linha (base 1) no documento. */
  line: number
  description: string
  affectedRows?: number
  hasPreview: boolean
  columns: string[]
  before: Cell[][]
  after: Cell[][]
}

export interface GuardInfo {
  guardId: string
  timeoutSeconds: number
  usesSavepoint: boolean
  previewUnavailable: boolean
  approximate: boolean
  totalAffected: number
  changes: GuardChange[]
}

export type ExecuteStatus =
  | 'completed' | 'error' | 'cancelled' | 'refused' | 'nothing'
  | 'needs_confirmation' | 'advise_transaction' | 'pending_decision' | 'tran_lost'

export interface ExecuteResponse {
  status: ExecuteStatus
  elapsedMs: number
  totalRows: number
  message?: string
  blocked?: { line: number; description: string }[]
  range?: DocRange
  guard?: GuardInfo
  /** Em needs_confirmation: MySQL/MariaDB com DDL, comando que confirma sozinho ou trecho opaco: roda sem transação e sem segunda confirmação (não tem volta). */
  irreversible?: boolean
}

export interface ExportDone { path: string; rows: number; elapsedMs: number }
export interface ExportProgressEvent { exportId: string; rows: number }
export interface MetadataUpdatedEvent { connectionId: string; phase: 'objects' | 'columns' | 'error'; message?: string }
export interface TabTransactionEvent { tabId: string; count: number }
export interface TabConnectionLostEvent { tabId: string; hadTransaction: boolean }
export interface GuardExpiredEvent { tabId: string; executionId: string; message: string }
export interface AppCloseRequestedEvent { tabs: { tabId: string; count: number }[] }

export interface QueryStartedEvent { tabId: string; executionId: string; range?: DocRange }
export interface QueryResultStartedEvent { tabId: string; executionId: string; resultIndex: number; source: DocRange; columns: ColumnInfo[] }
export interface QueryRowsEvent { tabId: string; executionId: string; resultIndex: number; rows: Cell[][] }
export interface QueryResultCompletedEvent { tabId: string; executionId: string; resultIndex: number; rowCount: number; truncated: boolean }
export interface QueryMessageEvent { tabId: string; executionId: string; kind: MessageKind; text: string; line?: number }

export type AiProviderId = 'anthropic' | 'openai' | 'gemini' | 'nvidia' | 'groq' | 'openrouter' | 'cerebras' | 'mistral' | 'ollama' | 'custom'

/** A chave de API nunca chega ao frontend: só `hasKey`. */
export interface AiProviderEntry { provider: AiProviderId; model: string; baseUrl?: string | null; hasKey: boolean }

/** Provedor ativo + a configuração guardada de cada provedor já usado (a chave de cada um é separada). */
export interface AiSettings { provider: AiProviderId; model: string; baseUrl?: string | null; hasKey: boolean; entries: AiProviderEntry[] }

/** `readOnly` é decidido pela trava do backend. Falso: o SQL deve entrar no editor comentado. */
export interface AiGenerateResponse { sql: string; notes?: string | null; readOnly: boolean; reason?: string | null }

// Mapa tipo -> { request, response }
export interface Requests {
  ping: { request: { message: string }; response: { message: string; serverTime: string } }
  'connections.list': { request: Record<string, never>; response: { connections: ConnectionInfo[] } }
  'connections.save': { request: SaveConnectionRequest; response: ConnectionInfo }
  'connections.delete': { request: { id: string }; response: Record<string, never> }
  'connections.duplicate': { request: { id: string }; response: ConnectionInfo }
  'connections.test': {
    request: { id?: string | null; settings: ConnectionSettings; password?: string | null }
    response: TestConnectionResult
  }
  'connections.parse': {
    request: { connectionString: string; provider?: ProviderId }
    response: { settings: ConnectionSettings; password?: string | null }
  }
  'connections.disconnect': { request: { id: string }; response: Record<string, never> }
  'tabs.open': {
    request: { tabId: string; connectionId: string; password?: string | null }
    response: { serverVersion: string; database: string }
  }
  'tabs.disconnect': { request: { tabId: string }; response: Record<string, never> }
  'query.execute': { request: ExecuteRequest; response: ExecuteResponse }
  'query.cancel': { request: { tabId: string }; response: Record<string, never> }
  'query.guard.resolve': { request: { tabId: string; guardId: string; commit: boolean }; response: { committed: boolean; message: string } }
  'tran.begin': { request: { tabId: string }; response: { tranCount: number } }
  'tran.commit': { request: { tabId: string }; response: { tranCount: number } }
  'tran.rollback': { request: { tabId: string }; response: { tranCount: number } }
  'metadata.refresh': { request: { connectionId: string; force: boolean }; response: { started: boolean; loading: boolean; loaded: boolean } }
  'metadata.get': { request: { connectionId: string }; response: import('./metadataIndex').MetadataDto }
  'export.pickPath': { request: { format: 'csv' | 'xlsx'; suggestedName: string }; response: { cancelled: boolean; path?: string } }
  'export.loaded': {
    request: { exportId: string; format: 'csv' | 'xlsx'; path: string; delimiter: string; columns: ColumnInfo[]; rows: Cell[][] }
    response: ExportDone
  }
  'export.rerun': {
    request: {
      exportId: string; tabId: string; sourceText: string; format: 'csv' | 'xlsx'; path: string; delimiter: string
      resultOrdinal: number; columnOrder: number[]
    }
    response: ExportDone
  }
  'export.cancel': { request: { exportId: string }; response: Record<string, never> }
  'export.openFile': { request: { path: string }; response: Record<string, never> }
  'export.showInFolder': { request: { path: string }; response: Record<string, never> }
  'ai.settings.get': { request: Record<string, never>; response: AiSettings }
  'ai.settings.save': { request: { provider: AiProviderId; model: string; baseUrl?: string | null; apiKey?: string | null; removeKey: boolean }; response: AiSettings }
  'ai.generate': { request: { tabId: string; connectionId: string; prompt: string }; response: AiGenerateResponse }
  'ai.test': { request: { provider: AiProviderId; model: string; baseUrl?: string | null; apiKey?: string | null }; response: { ok: boolean; message: string } }
  'ai.cancel': { request: { tabId: string }; response: Record<string, never> }
  'window.forceClose': { request: Record<string, never>; response: Record<string, never> }
  'session.load': { request: Record<string, never>; response: { state?: string | null } }
  'session.save': { request: { state: string }; response: Record<string, never> }
  'files.save': {
    request: { path?: string | null; suggestedName: string; content: string; saveAs: boolean }
    response: { cancelled: boolean; path?: string; name?: string }
  }
  'files.open': {
    request: Record<string, never>
    response: { cancelled: boolean; path?: string; name?: string; content?: string }
  }
  'window.minimize': { request: Record<string, never>; response: Record<string, never> }
  'window.toggleMaximize': { request: Record<string, never>; response: Record<string, never> }
  'window.close': { request: Record<string, never>; response: Record<string, never> }
  'connections.build': {
    request: { settings: ConnectionSettings; password?: string | null }
    response: { connectionString: string }
  }
}

// Mapa tipo de evento -> payload
export interface Events {
  [type: string]: unknown
}
