import { describe, expect, it } from 'vitest'
import { deserialize, initialTabsState, isDirty, retriesOnRun, serialize, tabsReducer, type TabsState } from './tabsState'

const add = (s: TabsState, id: string, connectionId: string | null = 'c1', extra = {}) =>
  tabsReducer(s, { type: 'add', id, connectionId, ...extra })

describe('tabsReducer', () => {
  it('numera as abas e ativa a nova', () => {
    let s = add(initialTabsState, 'a')
    s = add(s, 'b')
    expect(s.tabs.map((t) => t.title)).toEqual(['Query 1', 'Query 2'])
    expect(s.activeId).toBe('b')
  })

  it('não repete o número depois de fechar abas', () => {
    let s = add(add(initialTabsState, 'a'), 'b')
    s = tabsReducer(s, { type: 'close', id: 'b' })
    s = add(s, 'c')
    expect(s.tabs.map((t) => t.title)).toEqual(['Query 1', 'Query 3'])
  })

  it('aba aberta de arquivo usa o nome do arquivo e não consome o contador', () => {
    const s = add(initialTabsState, 'a', 'c1', { title: 'rel.sql', text: 'SELECT 1', filePath: 'C:\\rel.sql' })
    expect(s.tabs[0].title).toBe('rel.sql')
    expect(s.counter).toBe(0)
    expect(isDirty(s.tabs[0])).toBe(false)
  })

  it('fechar a aba ativa ativa a vizinha da direita, ou a da esquerda se era a última', () => {
    let s = add(add(add(initialTabsState, 'a'), 'b'), 'c')
    s = tabsReducer(s, { type: 'activate', id: 'b' })
    expect(tabsReducer(s, { type: 'close', id: 'b' }).activeId).toBe('c')
    s = tabsReducer(s, { type: 'activate', id: 'c' })
    expect(tabsReducer(s, { type: 'close', id: 'c' }).activeId).toBe('b')
    expect(tabsReducer(add(initialTabsState, 'x'), { type: 'close', id: 'x' }).activeId).toBeNull()
  })

  it('fechar uma aba inativa mantém a ativa', () => {
    const s = add(add(initialTabsState, 'a'), 'b')
    expect(tabsReducer(s, { type: 'close', id: 'a' }).activeId).toBe('b')
  })

  it('cycle dá a volta nos dois sentidos', () => {
    let s = add(add(add(initialTabsState, 'a'), 'b'), 'c') // ativa: c
    s = tabsReducer(s, { type: 'cycle', direction: 1 })
    expect(s.activeId).toBe('a')
    s = tabsReducer(s, { type: 'cycle', direction: -1 })
    expect(s.activeId).toBe('c')
  })

  it('texto alterado marca a aba como suja; salvar limpa', () => {
    let s = add(initialTabsState, 'a')
    s = tabsReducer(s, { type: 'setText', id: 'a', text: 'SELECT 1' })
    expect(isDirty(s.tabs[0])).toBe(true)
    s = tabsReducer(s, { type: 'saved', id: 'a', filePath: 'C:\\x.sql', title: 'x.sql' })
    expect(isDirty(s.tabs[0])).toBe(false)
    expect(s.tabs[0]).toMatchObject({ title: 'x.sql', filePath: 'C:\\x.sql' })
  })

  it('renomear ignora título vazio', () => {
    let s = add(initialTabsState, 'a')
    s = tabsReducer(s, { type: 'rename', id: 'a', title: '   ' })
    expect(s.tabs[0].title).toBe('Query 1')
    s = tabsReducer(s, { type: 'rename', id: 'a', title: ' Pedidos ' })
    expect(s.tabs[0].title).toBe('Pedidos')
  })

  it('mover reposiciona a aba sem mudar a ativa nem o conteúdo, e a ordem sobrevive à persistência', () => {
    let s = add(add(add(initialTabsState, 'a'), 'b'), 'c')
    s = tabsReducer(s, { type: 'setText', id: 'a', text: 'SELECT 1' })
    s = tabsReducer(s, { type: 'activate', id: 'b' })
    s = tabsReducer(s, { type: 'move', id: 'a', toIndex: 2 })
    expect(s.tabs.map((t) => t.id)).toEqual(['b', 'c', 'a'])
    expect(s.activeId).toBe('b')
    expect(s.tabs[2].text).toBe('SELECT 1')
    s = tabsReducer(s, { type: 'move', id: 'c', toIndex: 0 })
    expect(s.tabs.map((t) => t.id)).toEqual(['c', 'b', 'a'])
    expect(deserialize(serialize(s), new Set(['c1'])).tabs.map((t) => t.id)).toEqual(['c', 'b', 'a'])
  })

  it('mover para a mesma posição, para um índice fora da lista ou uma aba inexistente', () => {
    const s = add(add(initialTabsState, 'a'), 'b')
    expect(tabsReducer(s, { type: 'move', id: 'a', toIndex: 0 })).toBe(s)
    expect(tabsReducer(s, { type: 'move', id: 'x', toIndex: 1 })).toBe(s)
    expect(tabsReducer(s, { type: 'move', id: 'a', toIndex: 99 }).tabs.map((t) => t.id)).toEqual(['b', 'a'])
    expect(tabsReducer(s, { type: 'move', id: 'b', toIndex: -5 }).tabs.map((t) => t.id)).toEqual(['b', 'a'])
  })

  it('excluir uma conexão desanexa as abas dela, preservando o texto', () => {
    let s = add(add(initialTabsState, 'a', 'c1'), 'b', 'c2')
    s = tabsReducer(s, { type: 'setText', id: 'a', text: 'SELECT 1' })
    s = tabsReducer(s, { type: 'detachConnection', connectionId: 'c1' })
    expect(s.tabs[0]).toMatchObject({ connectionId: null, status: 'no-connection', text: 'SELECT 1' })
    expect(s.tabs[1].connectionId).toBe('c2')
  })
})

describe('persistência', () => {
  it('faz ida e volta preservando texto, alterações não salvas e aba ativa', () => {
    let s = add(add(initialTabsState, 'a', 'c1'), 'b', 'c2')
    s = tabsReducer(s, { type: 'setText', id: 'a', text: 'SELECT 1' })
    s = tabsReducer(s, { type: 'setStatus', id: 'a', status: 'connected' })
    s = tabsReducer(s, { type: 'activate', id: 'a' })
    const back = deserialize(serialize(s), new Set(['c1', 'c2']))
    expect(back.activeId).toBe('a')
    expect(back.counter).toBe(2)
    expect(back.tabs[0]).toMatchObject({ text: 'SELECT 1', savedText: '', status: 'idle', connectionId: 'c1' })
    expect(isDirty(back.tabs[0])).toBe(true)
  })

  it('nunca restaura como conectada', () => {
    const s = tabsReducer(add(initialTabsState, 'a'), { type: 'setStatus', id: 'a', status: 'connected' })
    expect(deserialize(serialize(s), new Set(['c1'])).tabs[0].status).toBe('idle')
  })

  it('conexão que não existe mais deixa a aba sem conexão, com o texto', () => {
    const s = tabsReducer(add(initialTabsState, 'a', 'sumiu'), { type: 'setText', id: 'a', text: 'x' })
    const back = deserialize(serialize(s), new Set(['c1']))
    expect(back.tabs[0]).toMatchObject({ connectionId: null, status: 'no-connection', text: 'x' })
    expect(back.tabs[0].statusMessage).toBeTruthy()
  })

  it.each([null, '', 'isto não é json', '{"version":2}', '{"version":1,"tabs":"x"}'])('JSON inválido (%s) vira estado vazio', (j) => {
    expect(deserialize(j, new Set())).toEqual(initialTabsState)
  })

  it('aba ativa inexistente cai na primeira', () => {
    const s = add(add(initialTabsState, 'a'), 'b')
    const json = JSON.stringify({ ...JSON.parse(serialize(s)), activeId: 'zzz' })
    expect(deserialize(json, new Set(['c1'])).activeId).toBe('a')
  })
})

describe('retriesOnRun', () => {
  const withStatus = (status: Parameters<typeof tabsReducer>[1] & { type: 'setStatus' }) => tabsReducer(add(initialTabsState, 'a'), status).tabs[0]

  it('aba cuja conexão falhou tenta conectar de novo ao executar', () => {
    expect(retriesOnRun(withStatus({ type: 'setStatus', id: 'a', status: 'error', message: 'servidor não encontrado' }))).toBe(true)
  })

  it('aba desconectada, conectando ou sem senha não reconecta sozinha', () => {
    for (const status of ['idle', 'disconnected', 'connecting', 'needs-password'] as const)
      expect(retriesOnRun(withStatus({ type: 'setStatus', id: 'a', status }))).toBe(false)
  })

  it('aba sem conexão escolhida não tem o que tentar', () => {
    expect(retriesOnRun(add(initialTabsState, 'a', null).tabs[0])).toBe(false)
  })
})
