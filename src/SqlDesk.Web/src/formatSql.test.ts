import { describe, expect, it } from 'vitest'
import { formatSql } from './formatSql'

const ok = (text: string) => {
  const r = formatSql(text)
  if (!r.ok) throw new Error('falhou: ' + r.reason)
  return r.text
}

describe('formatSql', () => {
  it('quebra em cláusulas, indenta e deixa as palavras-chave em maiúsculas', () => {
    expect(ok('select a,b from dbo.Clientes c inner join Pedidos p on p.ClienteId=c.Id where p.Total>500 order by p.Total desc')).toBe(
      [
        'SELECT', '    a,', '    b', 'FROM', '    dbo.Clientes c', '    INNER JOIN Pedidos p ON p.ClienteId = c.Id',
        'WHERE', '    p.Total > 500', 'ORDER BY', '    p.Total DESC',
      ].join('\n'),
    )
  })

  it('não muda a caixa de identificadores nem de textos', () => {
    const t = ok("select NomeCliente, 'Select * From X' as Txt from dbo.MinhaTabela")
    expect(t).toContain('NomeCliente')
    expect(t).toContain("'Select * From X'")
    expect(t).toContain('dbo.MinhaTabela')
  })

  it('preserva comentários de linha e de bloco', () => {
    const t = ok('select a -- coluna a\n, b /* coluna b */ from t -- fim')
    expect(t).toContain('-- coluna a')
    expect(t).toContain('/* coluna b */')
    expect(t).toContain('-- fim')
  })

  it('preserva identificadores entre colchetes e variáveis', () => {
    const t = ok('select [Nome Completo], @x from [dbo].[Minha Tabela] where id=@x')
    expect(t).toContain('[Nome Completo]')
    expect(t).toContain('[dbo].[Minha Tabela]')
    expect(t).toContain('@x')
  })

  it('formata cada batch separado por GO e mantém as linhas GO', () => {
    const t = ok('select 1\nGO\nselect a,b from t\ngo 3\nselect 2')
    const lines = t.split('\n')
    expect(lines.filter((l) => /^go( \d+)?$/i.test(l))).toEqual(['GO', 'go 3'])
    expect(t.indexOf('GO')).toBeLessThan(t.indexOf('FROM'))
    expect(t.match(/SELECT/g)).toHaveLength(3)
  })

  it('GO dentro de comentário ou texto não é separador', () => {
    const t = ok("select 1 /*\nGO\n*/ ,\n'GO' as x")
    expect(t).toMatch(/\/\*\s+GO\s+\*\//) // o comentário continua inteiro (só a indentação muda)
    expect(t).toContain("'GO'")
    expect(t.match(/^GO$/gm)).toBeNull() // nenhuma linha GO sozinha foi criada nem separou batches
  })

  it('é idempotente: formatar de novo não muda nada', () => {
    const once = ok('select a,b from t where x in (1,2,3) and y=\'z\' order by a')
    expect(ok(once)).toBe(once)
  })

  it('mantém a quebra de linha final e aceita CRLF', () => {
    expect(ok('select 1\n').endsWith('\n')).toBe(true)
    expect(ok('select a,\r\nb from t\r\n')).toContain('FROM')
  })

  it('texto vazio ou só espaços volta como está', () => {
    expect(formatSql('  \n ')).toEqual({ ok: true, text: '  \n ' })
  })

  it('statements de escrita são formatados sem perder o WHERE', () => {
    const t = ok("update dbo.Clientes set Nome='x', Ativo=1 where Id=5")
    expect(t).toMatch(/WHERE\s+Id = 5/)
    expect(t).toMatch(/SET/)
  })

  it('várias instruções separadas por ; ficam todas no resultado', () => {
    const t = ok('select 1; select 2; delete from t where id=1')
    expect(t.match(/SELECT/g)).toHaveLength(2)
    expect(t).toMatch(/DELETE FROM\s+t/)
  })
})

describe('formatSql: segurança', () => {
  it('recusa um resultado que mude o conteúdo (e não devolve texto)', () => {
    const r = formatSql('select a from t where x = 1', () => 'SELECT a FROM t')
    expect(r).toEqual({ ok: false, reason: 'o resultado mudaria o conteúdo do script' })
  })

  it('aceita diferenças só de espaço, quebra de linha e caixa', () => {
    const r = formatSql('select a from t', () => 'SELECT\n  a\nFROM\n  t')
    expect(r.ok).toBe(true)
  })

  it('erro do formatador vira recusa com o motivo, sem lançar', () => {
    const r = formatSql('select 1 from', () => {
      throw new Error('Parse error: Unexpected end of input')
    })
    expect(r.ok).toBe(false)
    if (!r.ok) expect(r.reason).toContain('Parse error')
  })

  it('uma falha em qualquer batch cancela tudo (nada é formatado pela metade)', () => {
    let n = 0
    const r = formatSql('select 1\nGO\nselect 2', (sql) => (++n === 2 ? 'DROP TABLE x' : sql.toUpperCase()))
    expect(r.ok).toBe(false)
  })
})

describe('formatSql: MySQL', () => {
  const my = (text: string) => {
    const r = formatSql(text, 'mysql')
    if (!r.ok) throw new Error('falhou: ' + r.reason)
    return r.text
  }

  it('preserva identificadores entre crases e não coloca colchetes', () => {
    const t = my('select `a`.`b` from `t`')
    expect(t).toContain('`a`.`b`')
    expect(t).toContain('`t`')
    expect(t).not.toMatch(/[[\]]/)
    expect(t).toMatch(/^SELECT/)
  })

  it('LIMIT, comentário # e string com barra invertida', () => {
    const t = my("select `nome completo` from clientes # todos\nwhere obs = 'it\\'s' limit 10")
    expect(t).toContain('`nome completo`')
    expect(t).toContain('# todos')
    expect(t).toContain("'it\\'s'")
    expect(t).toMatch(/LIMIT\s+10/)
  })

  it('GO não é separador no MySQL (vai para o formatador como texto comum)', () => {
    const seen: string[] = []
    formatSql('select 1\nGO\nselect 2', 'mysql', (sql) => {
      seen.push(sql)
      return sql
    })
    expect(seen).toEqual(['select 1\nGO\nselect 2'])
  })

  it('o dialeto padrão continua o do SQL Server', () => {
    expect(formatSql('select [a] from [t]').ok).toBe(true)
    expect(formatSql('select [a] from [t]', 'sqlserver')).toEqual(formatSql('select [a] from [t]'))
  })
})
