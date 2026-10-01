import { describe, expect, it } from 'vitest'
import { generateAlias, unquote } from './alias'

describe('generateAlias', () => {
  it('nome simples: primeira letra, em minúsculo', () => {
    expect(generateAlias('Clientes')).toBe('c')
    expect(generateAlias('clientes')).toBe('c')
    expect(generateAlias('CLIENTES')).toBe('c')
  })

  it('PascalCase: as letras maiúsculas, em minúsculo', () => {
    expect(generateAlias('PedidoItens')).toBe('pi')
    expect(generateAlias('ComunicacaoProcessual')).toBe('cp')
    expect(generateAlias('NotaFiscalEletronica')).toBe('nfe')
  })

  it('camelCase: primeira letra mais as maiúsculas', () => {
    expect(generateAlias('pedidoItens')).toBe('pi')
  })

  it('com "_": a primeira letra de cada parte', () => {
    expect(generateAlias('pedido_itens')).toBe('pi')
    expect(generateAlias('PEDIDO_ITENS_HIST')).toBe('pih')
    expect(generateAlias('_temp')).toBe('t')
  })

  it('conflito com alias já usado acrescenta número', () => {
    expect(generateAlias('Clientes', ['c'])).toBe('c1')
    expect(generateAlias('Clientes', ['c', 'c1'])).toBe('c2')
    expect(generateAlias('Clientes', ['C'])).toBe('c1') // sem diferenciar caixa
  })

  it('palavra reservada ganha número', () => {
    expect(generateAlias('InscricaoNacional')).toBe('in1')
    expect(generateAlias('OrdemServico')).toBe('os') // válido
    expect(generateAlias('Ordens')).toBe('o')
    expect(generateAlias('OrdemRecebida')).toBe('or1')
    expect(generateAlias('AuditoriaSistema')).toBe('as1')
    expect(generateAlias('Ocorrencias_Nacionais')).toBe('on1')
    expect(generateAlias('Item_Seguro')).toBe('is1')
    expect(generateAlias('GrupoOrdem')).toBe('go1')
    expect(generateAlias('TipoOperacao')).toBe('to1')
    expect(generateAlias('BaseYear')).toBe('by1')
  })

  it('reservada e em conflito continua incrementando', () => {
    expect(generateAlias('InscricaoNacional', ['in1'])).toBe('in2')
  })

  it('aceita nomes entre colchetes e sem letras', () => {
    expect(generateAlias('[Minha Tabela]')).toBe('mt')
    expect(generateAlias('[123]')).toBe('t')
    expect(generateAlias('Tabela2')).toBe('t')
  })

  it('acentos em minúsculo', () => {
    expect(generateAlias('Órgãos')).toBe('ó')
  })
})

describe('unquote', () => {
  it('remove colchetes e aspas', () => {
    expect(unquote('[Clientes]')).toBe('Clientes')
    expect(unquote('"Clientes"')).toBe('Clientes')
    expect(unquote('[a]]b]')).toBe('a]b')
    expect(unquote('Clientes')).toBe('Clientes')
  })
})
