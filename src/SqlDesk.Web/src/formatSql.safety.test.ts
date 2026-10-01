import { describe, expect, it } from 'vitest'
import { formatSql, sameContent } from './formatSql'

const ok = (text: string) => {
  const r = formatSql(text)
  if (!r.ok) throw new Error('falhou: ' + r.reason)
  return r.text
}

describe('formatSql: textos que não podem ser formatados com segurança', () => {
  it('string sem fechar é recusada (não se mexe no que o SQL Server leria como texto)', () => {
    expect(formatSql("select 'aberta from t")).toEqual({ ok: false, reason: 'há uma string ou um comentário sem fechar' })
  })

  it('comentário de bloco sem fechar é recusado', () => {
    expect(formatSql('select 1 /* aberto').ok).toBe(false)
  })

  it('não muda a caixa de identificadores que o formatador confundiria com palavra-chave', () => {
    const t = ok('select Name, Status, Date, First from dbo.Tab where Value = 1')
    expect(t).toContain('Name')
    expect(t).toContain('Status')
    expect(t).toContain('Date')
    expect(t).toContain('Value')
  })

  it('espaços dentro de textos entre aspas ficam intactos', () => {
    expect(ok("select 'a   b    c' as x")).toContain("'a   b    c'")
  })
})

describe('sameContent', () => {
  it('aceita só espaços, quebras de linha e caixa de palavra-chave', () => {
    expect(sameContent('select a from t where x=1', 'SELECT\n    a\nFROM\n    t\nWHERE\n    x = 1')).toBe(true)
  })

  it('recusa mudança de caixa em identificador', () => {
    expect(sameContent('select nome from t', 'SELECT NOME FROM t')).toBe(false)
  })

  it('recusa mudança em texto entre aspas, inclusive nos espaços', () => {
    expect(sameContent("select 'a  b'", "SELECT 'a b'")).toBe(false)
    expect(sameContent("select 'abc'", "SELECT 'ABC'")).toBe(false)
  })

  it('recusa token a mais ou a menos e número diferente', () => {
    expect(sameContent('select 1', 'SELECT 1 ,')).toBe(false)
    expect(sameContent('select 100', 'SELECT 10')).toBe(false)
  })

  it('comentário pode mudar de indentação, não de conteúdo', () => {
    expect(sameContent('select 1 /* a\nb */', 'SELECT 1 /* a\n    b */')).toBe(true)
    expect(sameContent('select 1 -- nota', 'SELECT 1 -- outra')).toBe(false)
  })
})
