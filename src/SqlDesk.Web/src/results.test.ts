import { describe, expect, it } from 'vitest'
import type { ColumnInfo, ExecuteResponse } from './contracts'
import { applyEvent, beginRun, emptyResults, failRun, finishRun, MESSAGES_TAB, type ResultEvent, type TabResults } from './results'

const cols: ColumnInfo[] = [{ name: 'Id', kind: 'number', typeName: 'int' }]
const done: ExecuteResponse = { status: 'completed', elapsedMs: 5, totalRows: 2 }
const base = { tabId: 't' }

function run(state: TabResults, id: string, text: string, events: ResultEvent[], keep = false): TabResults {
  let s = beginRun(state, id, text, keep)
  for (const e of events) s = applyEvent(s, e)
  return s
}

const started = (id: string): ResultEvent => ({ type: 'started', payload: { ...base, executionId: id } })
const resultStarted = (id: string, index: number, start = 0, length = 8): ResultEvent => ({
  type: 'resultStarted',
  payload: { ...base, executionId: id, resultIndex: index, source: { start, length }, columns: cols },
})
const rows = (id: string, index: number, values: number[]): ResultEvent => ({
  type: 'rows',
  payload: { ...base, executionId: id, resultIndex: index, rows: values.map((v) => [v]) },
})
const completed = (id: string, index: number, rowCount: number, truncated = false): ResultEvent => ({
  type: 'resultCompleted',
  payload: { ...base, executionId: id, resultIndex: index, rowCount, truncated },
})
const message = (id: string, text: string, kind: 'info' | 'error' = 'info', line?: number): ResultEvent => ({
  type: 'message',
  payload: { ...base, executionId: id, kind, text, line },
})

describe('resultados', () => {
  it('acumula result sets e linhas em lotes', () => {
    const s = run(emptyResults, 'e1', 'SELECT 1', [
      started('e1'),
      resultStarted('e1', 0),
      rows('e1', 0, [1, 2]),
      rows('e1', 0, [3]),
      completed('e1', 0, 3),
    ])
    expect(s.sets).toHaveLength(1)
    expect(s.sets[0].title).toBe('Resultado 1')
    expect(s.sets[0].rows).toEqual([[1], [2], [3]])
    expect(s.sets[0].rowCount).toBe(3)
    expect(s.sets[0].done).toBe(true)
    expect(s.active).toBe(s.sets[0].key)
  })

  it('vários result sets viram sub-abas; só o primeiro assume o foco', () => {
    const s = run(emptyResults, 'e1', 'SELECT 1; SELECT 2', [
      started('e1'),
      resultStarted('e1', 0),
      completed('e1', 0, 0),
      resultStarted('e1', 1),
      completed('e1', 1, 0),
    ])
    expect(s.sets.map((x) => x.title)).toEqual(['Resultado 1', 'Resultado 2'])
    expect(s.active).toBe(s.sets[0].key)
  })

  it('substitui os resultados anteriores numa nova execução comum', () => {
    let s = run(emptyResults, 'e1', 'a', [started('e1'), resultStarted('e1', 0), message('e1', 'ok')])
    s = finishRun(s, 'e1', done)
    s = run(s, 'e2', 'b', [started('e2'), resultStarted('e2', 0)])
    expect(s.sets).toHaveLength(1)
    expect(s.sets[0].key).toBe('e2:0')
    expect(s.sets[0].title).toBe('Resultado 1')
    expect(s.messages).toHaveLength(0)
  })

  it('Ctrl+\\ preserva as sub-abas anteriores e continua a numeração', () => {
    let s = run(emptyResults, 'e1', 'a', [started('e1'), resultStarted('e1', 0)])
    s = finishRun(s, 'e1', done)
    s = run(s, 'e2', 'b', [started('e2'), resultStarted('e2', 0)], true)
    expect(s.sets.map((x) => x.title)).toEqual(['Resultado 1', 'Resultado 2'])
    expect(s.active).toBe('e2:0')
  })

  it('execução sem nada para rodar não apaga os resultados anteriores', () => {
    let s = run(emptyResults, 'e1', 'a', [started('e1'), resultStarted('e1', 0)])
    s = finishRun(s, 'e1', done)
    s = beginRun(s, 'e2', 'b', false)
    s = finishRun(s, 'e2', { status: 'nothing', elapsedMs: 0, totalRows: 0, message: 'vazio' })
    expect(s.sets).toHaveLength(1)
    expect(s.running).toBe(false)
    expect(s.lastRun?.status).toBe('completed')
  })

  it('descarta eventos de uma execução antiga', () => {
    let s = beginRun(emptyResults, 'e2', 'b', false)
    s = applyEvent(s, resultStarted('e1', 0))
    s = applyEvent(s, message('e1', 'tardio'))
    expect(s.sets).toHaveLength(0)
    expect(s.messages).toHaveLength(0)
  })

  it('guarda o texto de origem de cada result set, para "Carregar todas"', () => {
    const s = run(emptyResults, 'e1', 'SELECT 1;\nSELECT 2;', [started('e1'), resultStarted('e1', 0, 10, 9)])
    expect(s.sets[0].sourceText).toBe('SELECT 2;')
    expect(s.sets[0].source).toEqual({ start: 10, length: 9 })
  })

  it('marca o resultado truncado', () => {
    const s = run(emptyResults, 'e1', 'x', [started('e1'), resultStarted('e1', 0), completed('e1', 0, 10_000, true)])
    expect(s.sets[0].truncated).toBe(true)
  })

  it('sem result sets, ao terminar mostra a aba Mensagens; com erro e linha, guarda a linha', () => {
    let s = run(emptyResults, 'e1', 'x', [started('e1'), message('e1', 'Sintaxe incorreta', 'error', 7)])
    s = finishRun(s, 'e1', { status: 'error', elapsedMs: 3, totalRows: 0 })
    expect(s.active).toBe(MESSAGES_TAB)
    expect(s.messages[0]).toMatchObject({ kind: 'error', line: 7 })
    expect(s.lastRun).toEqual({ status: 'error', elapsedMs: 3, totalRows: 0 })
  })

  it('falha da requisição vira mensagem de erro e encerra a execução', () => {
    let s = beginRun(emptyResults, 'e1', 'x', false)
    s = failRun(s, 'e1', 'A aba não está conectada.')
    expect(s.running).toBe(false)
    expect(s.messages[0].text).toBe('A aba não está conectada.')
    expect(s.active).toBe(MESSAGES_TAB)
  })

  it('aceita 10 mil linhas em lotes sem copiar o array a cada lote', () => {
    let s = beginRun(emptyResults, 'e1', 'x', false)
    s = applyEvent(s, started('e1'))
    s = applyEvent(s, resultStarted('e1', 0))
    const arr = s.sets[0].rows
    for (let i = 0; i < 20; i++) s = applyEvent(s, rows('e1', 0, Array.from({ length: 500 }, (_, k) => i * 500 + k)))
    expect(s.sets[0].rows).toBe(arr)
    expect(s.sets[0].rowCount).toBe(10_000)
  })
})
