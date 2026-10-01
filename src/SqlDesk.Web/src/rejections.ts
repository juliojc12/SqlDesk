interface ErrorLike {
  name?: unknown
  message?: unknown
  /** Presente nos erros das chamadas à ponte (`BridgeCallError`); verificado pela forma para não importar a ponte (que exige o navegador). */
  detail?: { code?: unknown; message?: unknown }
}

/**
 * Cancelamentos são o fim normal de uma operação interrompida (o Monaco rejeita a promessa de uma sugestão ou de uma
 * formatação cancelada com `Error('Canceled')`, uma chamada abortada vira `AbortError`), não uma falha: não merecem aviso.
 */
export function isCancellation(reason: unknown): boolean {
  const r = (reason ?? {}) as ErrorLike
  if (typeof r.detail?.code === 'string') return r.detail.code === 'cancelled'
  return r.name === 'Canceled' || r.name === 'AbortError' || r.message === 'Canceled'
}

/** Texto do aviso para uma promessa rejeitada sem tratamento, ou `null` quando não há nada a avisar. */
export function describeRejection(reason: unknown): string | null {
  if (isCancellation(reason)) return null
  const r = (reason ?? {}) as ErrorLike
  const detail = typeof r.detail?.message === 'string' ? r.detail.message : reason instanceof Error ? reason.message : String(reason ?? '')
  return `Ocorreu um erro inesperado na interface${detail ? `: ${detail}` : '.'}`
}

/** Quanto tempo um aviso de erro não crítico fica na tela: o suficiente para ler (cerca de 7 s, mais um pouco para mensagens longas). */
export function errorDisplayMs(text: string): number {
  return Math.min(20_000, 7_000 + text.length * 40)
}
