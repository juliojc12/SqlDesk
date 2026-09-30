import type { BridgeError, Envelope, Requests } from './contracts'

type Handler = (payload: unknown) => void

export class BridgeCallError extends Error {
  readonly detail: BridgeError
  constructor(detail: BridgeError) {
    super(detail.message)
    this.detail = detail
  }
}

interface WebView {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (e: MessageEvent) => void): void
}

const webview: WebView | undefined = (window as unknown as { chrome?: { webview?: WebView } }).chrome?.webview

const pending = new Map<string, { resolve: (v: unknown) => void; reject: (e: unknown) => void }>()
const handlers = new Map<string, Set<Handler>>()

webview?.addEventListener('message', (e) => {
  const msg = (typeof e.data === 'string' ? JSON.parse(e.data) : e.data) as Envelope<unknown>
  if (msg.id !== null && msg.id !== undefined) {
    const p = pending.get(msg.id)
    if (!p) return
    pending.delete(msg.id)
    const err = (msg.payload as { error?: BridgeError } | null)?.error
    if (err) p.reject(new BridgeCallError(err))
    else p.resolve(msg.payload)
    return
  }
  handlers.get(msg.type)?.forEach((h) => h(msg.payload))
})

export function invoke<K extends keyof Requests>(
  type: K,
  payload: Requests[K]['request'],
): Promise<Requests[K]['response']> {
  if (!webview) {
    return Promise.reject(new BridgeCallError({ code: 'no_host', message: 'Executando fora do host WPF (WebView2 indisponível).' }))
  }
  const id = crypto.randomUUID()
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve: resolve as (v: unknown) => void, reject })
    webview.postMessage({ id, type, payload } satisfies Envelope)
  })
}

export function on<T = unknown>(type: string, handler: (payload: T) => void): () => void {
  let set = handlers.get(type)
  if (!set) handlers.set(type, (set = new Set()))
  set.add(handler as Handler)
  return () => set.delete(handler as Handler)
}
