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

// Mapa tipo -> { request, response }
export interface Requests {
  ping: { request: { message: string }; response: { message: string; serverTime: string } }
}

// Mapa tipo de evento -> payload
export interface Events {
  [type: string]: unknown
}
