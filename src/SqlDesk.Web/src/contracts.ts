// Contratos da ponte JS/C#. Espelham os records em SqlDesk.Host/Bridge/Contracts.cs.

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
    request: { connectionString: string }
    response: { settings: ConnectionSettings; password?: string | null }
  }
  'connections.disconnect': { request: { id: string }; response: Record<string, never> }
  'tabs.open': {
    request: { tabId: string; connectionId: string; password?: string | null }
    response: { serverVersion: string; database: string }
  }
  'tabs.disconnect': { request: { tabId: string }; response: Record<string, never> }
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
