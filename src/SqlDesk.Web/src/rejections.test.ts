import { describe, expect, it } from 'vitest'
import { describeRejection, errorDisplayMs, isCancellation } from './rejections'

const bridgeError = (code: string, message: string) => Object.assign(new Error(message), { detail: { code, message } })

describe('isCancellation', () => {
  it('reconhece o cancelamento do Monaco (Error "Canceled")', () => {
    const e = new Error('Canceled')
    e.name = 'Canceled'
    expect(isCancellation(e)).toBe(true)
    expect(isCancellation(new Error('Canceled'))).toBe(true)
    expect(isCancellation({ name: 'Canceled' })).toBe(true)
  })

  it('reconhece chamada abortada e cancelamento vindo do backend', () => {
    const abort = new Error('x')
    abort.name = 'AbortError'
    expect(isCancellation(abort)).toBe(true)
    expect(isCancellation(bridgeError('cancelled', 'Operação cancelada.'))).toBe(true)
  })

  it('erros de verdade não são cancelamento', () => {
    expect(isCancellation(new Error('falhou'))).toBe(false)
    expect(isCancellation(bridgeError('internal_error', 'x'))).toBe(false)
    expect(isCancellation(undefined)).toBe(false)
    expect(isCancellation(null)).toBe(false)
    expect(isCancellation('Canceled by user')).toBe(false)
  })
})

describe('describeRejection', () => {
  it('cancelamento não gera aviso', () => {
    const e = new Error('Canceled')
    e.name = 'Canceled'
    expect(describeRejection(e)).toBeNull()
  })

  it('erro real vira uma frase clara com o motivo', () => {
    expect(describeRejection(new Error('Cannot read properties of undefined'))).toBe(
      'Ocorreu um erro inesperado na interface: Cannot read properties of undefined',
    )
    expect(describeRejection('texto solto')).toBe('Ocorreu um erro inesperado na interface: texto solto')
  })

  it('erro da ponte mostra a mensagem do backend', () => {
    expect(describeRejection(bridgeError('not_connected', 'A aba não está conectada.'))).toBe(
      'Ocorreu um erro inesperado na interface: A aba não está conectada.',
    )
  })

  it('sem detalhe, ainda explica que foi um erro de interface', () => {
    expect(describeRejection(undefined)).toBe('Ocorreu um erro inesperado na interface.')
  })

  it('nunca mostra o formato confuso "Nome: Nome" de um erro convertido em texto', () => {
    const e = new Error('Canceled')
    e.name = 'Canceled'
    expect(describeRejection(e)).toBeNull()
    expect(String(describeRejection(new TypeError('x')))).not.toMatch(/TypeError: TypeError/)
  })
})

describe('errorDisplayMs', () => {
  it('fica entre 7 e 20 segundos, crescendo com o tamanho da mensagem', () => {
    expect(errorDisplayMs('curto')).toBeGreaterThanOrEqual(7_000)
    expect(errorDisplayMs('x'.repeat(100))).toBeGreaterThan(errorDisplayMs('curto'))
    expect(errorDisplayMs('x'.repeat(10_000))).toBe(20_000)
  })
})
