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

describe('formatSql MySQL: comentários executáveis', () => {
  // /*! */, /*!NNNNN */, /*M! */ e /*+ */ são executados (ou lidos) pelo MySQL/MariaDB: nem a indentação pode mudar.
  it('não muda o conteúdo de /*!NNNNN ... */ (o valor gravado mudaria): recusa', () => {
    expect(formatSql("/*!50000 insert into t values ('a\n      b') */", 'mysql').ok).toBe(false)
  })

  it('não reindenta string dentro de /*!50000 ... */ no meio do SELECT: recusa', () => {
    expect(formatSql("select 1 /*!50000 , 'a\n   b' */ from t", 'mysql').ok).toBe(false)
  })

  // Dentro de /*! */ o MySQL lê SQL normal: um */ dentro de string, de comentário de linha ou de /* */ aninhado não fecha o
  // comentário. Sem como provar onde ele termina, a formatação é recusada.
  const probes = [
    "select /*!50000 'x*/' , 'a\n   b' */ 1 -- '\nfrom t",
    'select 1 /*! /* x */ , 2 */ from t',
    'select 1 /*!50000 -- */\n , 2 */ from t',
    'select 1 /*!50000 # */\n , 2 */ from t',
    'select 1 /*M! "x" */ from t',
    'select 1 /*!50000 `a` */ from t',
    "select 1 /*!50000 'a\\'b' */ from t",
  ]
  for (const p of probes) {
    it(`recusa comentário executável cujo fim não dá para provar: ${JSON.stringify(p)}`, () => {
      expect(formatSql(p, 'mysql').ok).toBe(false)
    })
  }

  it('dica de otimizador continua formatando, idêntica', () => {
    const a = formatSql('select /*+ MAX_EXECUTION_TIME(1000) */ a from t where x = 1', 'mysql')
    expect(a.ok).toBe(true)
    if (a.ok) expect(a.text).toContain('/*+ MAX_EXECUTION_TIME(1000) */')
  })

  it('dica de otimizador não esconde o vizinho: "delete /*+ x */ from commit" não vira COMMIT', () => {
    const r = formatSql('delete /*+ x */ from commit where id = 1', 'mysql')
    expect(r.ok ? /\bCOMMIT\b/.test(r.text) : false).toBe(false)
    expect(r.ok).toBe(false)
  })

  // O corpo de /*! */ e /*M! */ é SQL que o MySQL/MariaDB executa e que a verificação não enxerga: qualquer um recusa.
  const executed = [
    'select 1 /*!50000 , 2 */ from t',
    'select 1 /*M!100100 , 2 */ from t',
    '/*!50000 delete from */ commit where id = 1',
    '/*!50000 truncate table */ commit',
    '/*!50000 update */ commit set a = 1',
    '/*!50000 delete from */ rollback where id = 1',
    'select * from loja /*!50000 . */ order',
    'select * from loja/*!.*/order',
  ]
  for (const t of executed) {
    it(`recusa texto com comentário executável: ${JSON.stringify(t)}`, () => {
      expect(formatSql(t, 'mysql').ok).toBe(false)
    })
  }

  it('sameContent no MySQL exige igualdade exata em /*! */, /*M! */ e /*+ */', () => {
    expect(sameContent('select 1 /*! a\nb */', 'SELECT 1 /*! a\n    b */', 'mysql')).toBe(false)
    expect(sameContent('select 1 /*M! a\nb */', 'SELECT 1 /*M! a\n    b */', 'mysql')).toBe(false)
    expect(sameContent('select /*+ a\nb */ 1', 'SELECT /*+ a\n    b */ 1', 'mysql')).toBe(false)
    // comentário comum continua podendo mudar de indentação
    expect(sameContent('select 1 /* a\nb */', 'SELECT 1 /* a\n    b */', 'mysql')).toBe(true)
  })
})

describe('formatSql MySQL: nome de tabela que parece palavra-chave', () => {
  // Nomes de tabela/banco/alias diferenciam maiúsculas no MySQL em Linux: OFFSET seria outra tabela.
  const names = ['offset', 'modify', 'end', 'commit', 'rollback', 'execute', 'shutdown']
  for (const n of names) {
    it(`"delete from ${n} where id = 1" não vira outra tabela: recusa`, () => {
      expect(formatSql(`delete from ${n} where id = 1`, 'mysql').ok).toBe(false)
    })
  }

  // Reservadas só em alguma versão (MySQL 8, MariaDB...): em outra versão são nomes válidos sem crases.
  const versionSpecific = [
    'delete from groups where id = 1',
    'delete from system where id = 1',
    'delete from cube where id = 1',
    'delete from lateral where id = 1',
    'delete from recursive where id = 1',
    'delete from rows where id = 1',
    'delete from row where id = 1',
    'delete from of where id = 1',
    'delete from function where id = 1',
    'select * from window w',
    'select * from t1 join t2 empty on empty.id = t1.id',
    'delete from optimizer_costs where id = 1',
  ]
  for (const t of versionSpecific) {
    it(`palavra reservada só em algumas versões não muda de caixa (recusa): ${t}`, () => {
      expect(formatSql(t, 'mysql')).toEqual({ ok: false, reason: 'o resultado mudaria o conteúdo do script' })
    })
  }

  it('nome depois de ponto (banco.tabela) nunca muda de caixa, nem sendo reservada', () => {
    expect(sameContent('select * from loja.order', 'SELECT * FROM loja.ORDER', 'mysql')).toBe(false)
    expect(sameContent('select * from offset.t', 'SELECT * FROM OFFSET.t', 'mysql')).toBe(false)
  })

  it('consultas comuns continuam formatando com as palavras-chave em maiúsculas', () => {
    const ok = (t: string) => {
      const r = formatSql(t, 'mysql')
      if (!r.ok) throw new Error('falhou: ' + r.reason)
      return r.text
    }
    expect(ok('select a, count(*) from t join u on u.id = t.id where x = 1 group by a order by a desc limit 10 offset 5')).toMatch(
      /^SELECT[\s\S]*FROM[\s\S]*JOIN[\s\S]*ON[\s\S]*WHERE[\s\S]*GROUP BY[\s\S]*ORDER BY[\s\S]*DESC[\s\S]*LIMIT\s+10\s+OFFSET\s+5$/,
    )
    expect(ok("insert into t (a) values (1) on duplicate key update a = 2")).toMatch(/INSERT INTO[\s\S]*VALUES[\s\S]*ON DUPLICATE KEY UPDATE/)
    expect(ok('update t set a = 1 where id = 2')).toMatch(/UPDATE t\s+SET[\s\S]*WHERE/)
    expect(ok('delete from t where id = 2')).toMatch(/DELETE FROM t\s+WHERE/)
    expect(ok('select case when a = 1 then 2 else 3 end as x from t')).toMatch(/CASE[\s\S]*WHEN[\s\S]*THEN[\s\S]*ELSE[\s\S]*END/)
    expect(ok('start transaction; delete from t where id = 1; commit')).toMatch(/^START TRANSACTION;[\s\S]*COMMIT$/)
    expect(ok('begin; rollback')).toMatch(/^begin;\s+ROLLBACK$/i) // o formatador deixa o BEGIN como está
  })
})
