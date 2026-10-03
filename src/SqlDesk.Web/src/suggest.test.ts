import { describe, expect, it } from 'vitest'
import { buildIndex, type MetadataDto } from './metadataIndex'
import { analyze, detectContext, parseTableRefs, scan, statementBounds } from './sqlContext'
import { displayName, quoteIfNeeded, suggest } from './suggest'
import { unquote } from './alias'

const dto: MetadataDto = {
  loaded: true, columnsLoaded: true, loading: false,
  schemas: ['dbo', 'vendas'],
  objects: [
    { schema: 'dbo', name: 'Clientes', type: 'table' },
    { schema: 'dbo', name: 'PedidoItens', type: 'table' },
    { schema: 'dbo', name: 'vwAtivos', type: 'view' },
    { schema: 'dbo', name: 'pr_Limpa', type: 'procedure' },
    { schema: 'vendas', name: 'Pedidos', type: 'table' },
    { schema: 'vendas', name: 'Clientes', type: 'table' },
    { schema: 'dbo', name: 'InscricaoNacional', type: 'table' },
  ],
  columns: {
    'dbo.Clientes': [{ name: 'Id', type: 'int', nullable: false }, { name: 'Nome', type: 'nvarchar(50)', nullable: true }],
    'dbo.PedidoItens': [{ name: 'Id', type: 'int', nullable: false }, { name: 'ClienteId', type: 'int', nullable: false }, { name: 'Qtd', type: 'int', nullable: true }],
    'vendas.Pedidos': [{ name: 'Numero', type: 'int', nullable: false }],
    'vendas.Clientes': [{ name: 'Codigo', type: 'int', nullable: false }],
    'dbo.vwAtivos': [{ name: 'Ativo', type: 'bit', nullable: false }],
  },
}
const index = buildIndex(dto)
const on = { autoAlias: true }
const off = { autoAlias: false }

/** Marca o cursor com `|` no texto. */
function at(sqlWithCursor: string, opts = on, idx = index) {
  const offset = sqlWithCursor.indexOf('|')
  const text = sqlWithCursor.replace('|', '')
  return suggest(text, offset, idx, opts)
}
const labels = (r: { items: { label: string }[] }) => r.items.map((i) => i.label)

describe('scan', () => {
  it('mascara comentários e strings mantendo o tamanho', () => {
    const s = "SELECT 'UPDATE' -- DROP\n/* DELETE */ x"
    const { masked } = scan(s)
    expect(masked.length).toBe(s.length)
    expect(masked).not.toMatch(/UPDATE|DROP|DELETE/)
    expect(masked).toContain('SELECT')
    expect(masked.endsWith(' x')).toBe(true)
  })

  it('"" dentro da string não a encerra e [colchetes] são preservados', () => {
    expect(scan("'a''b' FROM [Minha Tabela]").masked).toBe("       FROM [Minha Tabela]")
  })

  it('informa se o cursor está em comentário ou string', () => {
    expect(scan("SELECT 'abc", 10).inside).toBe(true)
    expect(scan('SELECT 1 -- nota', 15).inside).toBe(true)
    expect(scan('SELECT 1 /* x */ ', 17).inside).toBe(false)
    expect(scan('SELECT 1', 8).inside).toBe(false)
  })
})

describe('statementBounds', () => {
  const b = (t: string, c: number) => {
    const r = statementBounds(t, c)
    return t.slice(r.start, r.end).trim()
  }
  it('separa por ;, por GO e por linha em branco', () => {
    expect(b('SELECT 1; SELECT 2; SELECT 3', 14)).toBe('SELECT 2')
    expect(b('SELECT 1\nGO\nSELECT 2', 18)).toBe('SELECT 2')
    expect(b('SELECT 1\n\nSELECT 2\n\nSELECT 3', 12)).toBe('SELECT 2')
  })
  it('sem separadores, é o texto todo', () => {
    expect(b('SELECT *\nFROM x\nWHERE y', 10)).toBe('SELECT *\nFROM x\nWHERE y')
  })
})

describe('parseTableRefs', () => {
  it('FROM e JOIN com e sem alias, com e sem AS', () => {
    expect(parseTableRefs('SELECT * FROM dbo.Clientes c INNER JOIN vendas.Pedidos AS p ON 1=1 LEFT JOIN PedidoItens ON 2=2 WHERE 1=1')).toEqual([
      { schema: 'dbo', name: 'Clientes', alias: 'c' },
      { schema: 'vendas', name: 'Pedidos', alias: 'p' },
      { schema: undefined, name: 'PedidoItens', alias: undefined },
    ])
  })
  it('palavra reservada depois do nome não é alias', () => {
    expect(parseTableRefs('SELECT * FROM Clientes WHERE a = 1')[0].alias).toBeUndefined()
    expect(parseTableRefs('SELECT * FROM Clientes ORDER BY a')[0].alias).toBeUndefined()
    expect(parseTableRefs('SELECT * FROM Clientes INNER JOIN Pedidos ON 1=1').map((r) => r.alias)).toEqual([undefined, undefined])
  })
  it('lista separada por vírgula', () => {
    expect(parseTableRefs('SELECT * FROM Clientes c, Pedidos p, PedidoItens WHERE 1=1').map((r) => [r.name, r.alias])).toEqual([
      ['Clientes', 'c'], ['Pedidos', 'p'], ['PedidoItens', undefined],
    ])
  })
  it('UPDATE, INTO e identificadores entre colchetes', () => {
    expect(parseTableRefs('UPDATE [Minha Tabela] SET a = 1').map((r) => r.name)).toEqual(['Minha Tabela'])
    expect(parseTableRefs('INSERT INTO dbo.Clientes (a) VALUES (1)')[0]).toMatchObject({ schema: 'dbo', name: 'Clientes' })
  })
})

describe('detectContext', () => {
  const ctx = (s: string) => detectContext(s)
  it('depois de FROM, JOIN, UPDATE, INTO: tabelas', () => {
    expect(ctx('SELECT * FROM ')).toEqual({ kind: 'table', keyword: 'FROM' })
    expect(ctx('SELECT * FROM Cli')).toEqual({ kind: 'table', keyword: 'FROM' })
    expect(ctx('SELECT * FROM a INNER JOIN ')).toEqual({ kind: 'table', keyword: 'JOIN' })
    expect(ctx('UPDATE ')).toEqual({ kind: 'table', keyword: 'UPDATE' })
    expect(ctx('INSERT INTO ')).toEqual({ kind: 'table', keyword: 'INTO' })
    expect(ctx('DELETE FROM ')).toEqual({ kind: 'table', keyword: 'DELETE FROM' })
    expect(ctx('SELECT * FROM a, ')).toEqual({ kind: 'table', keyword: 'FROM' })
  })
  it('depois de um nome completo de tabela, o que vem é alias ou palavra-chave', () => {
    expect(ctx('SELECT * FROM Clientes ')).toEqual({ kind: 'other' })
    expect(ctx('SELECT * FROM Clientes c ')).toEqual({ kind: 'other' })
  })
  it('SELECT, WHERE, ON, GROUP/ORDER BY, SET: colunas', () => {
    for (const s of ['SELECT ', 'SELECT a, ', 'SELECT * FROM x WHERE ', 'SELECT * FROM x WHERE a = 1 AND ', 'SELECT * FROM x a JOIN y b ON ',
      'SELECT a FROM x GROUP BY ', 'SELECT a FROM x ORDER BY ', 'UPDATE x SET ', 'UPDATE x SET a = 1, ', 'SELECT * FROM x HAVING '])
      expect(ctx(s)).toEqual({ kind: 'columns' })
  })
  it('subconsulta entre parênteses não confunde o contexto de fora', () => {
    expect(ctx('SELECT * FROM (SELECT 1 AS a) t WHERE ')).toEqual({ kind: 'columns' })
    expect(ctx('SELECT * FROM x WHERE id IN (SELECT ')).toEqual({ kind: 'columns' })
    expect(ctx('SELECT * FROM x WHERE id IN (SELECT id FROM ')).toEqual({ kind: 'table', keyword: 'FROM' })
  })
  it('EXEC: procedures', () => {
    expect(ctx('EXEC ')).toEqual({ kind: 'exec' })
    expect(ctx('exec dbo.pr')).toEqual({ kind: 'qualified', parts: ['dbo'] })
  })
  it('alias. e schema.: qualificado', () => {
    expect(ctx('SELECT c.')).toEqual({ kind: 'qualified', parts: ['c'] })
    expect(ctx('SELECT c.Nom')).toEqual({ kind: 'qualified', parts: ['c'] })
    expect(ctx('SELECT * FROM vendas.')).toEqual({ kind: 'qualified', parts: ['vendas'] })
    expect(ctx('SELECT * FROM dbo.Clientes.')).toEqual({ kind: 'qualified', parts: ['dbo', 'Clientes'] })
    expect(ctx('SELECT [Meu Alias].')).toEqual({ kind: 'qualified', parts: ['Meu Alias'] })
  })
  it('início de statement e depois de ; : outros', () => {
    expect(ctx('')).toEqual({ kind: 'other' })
    expect(ctx('SELECT 1; ')).toEqual({ kind: 'other' })
  })
})

describe('analyze', () => {
  it('vê as tabelas do statement inteiro, inclusive as que vêm depois do cursor', () => {
    const text = 'SELECT c. FROM Clientes c'
    const a = analyze(text, 'SELECT c.'.length)
    expect(a.refs).toEqual([{ schema: undefined, name: 'Clientes', alias: 'c' }])
  })
  it('só considera o statement atual', () => {
    const text = 'SELECT * FROM Clientes c;\nSELECT p. FROM Pedidos p'
    const a = analyze(text, text.indexOf('p.') + 2)
    expect(a.refs.map((r) => r.name)).toEqual(['Pedidos'])
  })
  it('dentro de comentário ou string, sem sugestão', () => {
    expect(analyze("SELECT 'abc", 11).inside).toBe(true)
  })
})

describe('suggest: tabelas (FROM/JOIN)', () => {
  it('lista tabelas e views, dbo sem prefixo e outros schemas com prefixo', () => {
    const l = labels(at('SELECT * FROM |', off))
    expect(l).toEqual(expect.arrayContaining(['Clientes', 'PedidoItens', 'vwAtivos', 'vendas.Pedidos', 'vendas.Clientes', 'vendas']))
    expect(l).not.toContain('pr_Limpa') // procedure não entra
  })

  it('insere o alias automático em FROM e JOIN', () => {
    const r = at('SELECT * FROM |')
    expect(r.items.find((i) => i.label === 'Clientes')?.insertText).toBe('Clientes c')
    expect(r.items.find((i) => i.label === 'PedidoItens')?.insertText).toBe('PedidoItens pi')
    expect(r.items.find((i) => i.label === 'vendas.Pedidos')?.insertText).toBe('vendas.Pedidos p')
    expect(at('SELECT * FROM Clientes c JOIN |').items.find((i) => i.label === 'PedidoItens')?.insertText).toBe('PedidoItens pi')
  })

  it('conflito com alias já usado no statement vira c1', () => {
    const r = at('SELECT * FROM Clientes c INNER JOIN |')
    expect(r.items.find((i) => i.label === 'vendas.Clientes')?.insertText).toBe('vendas.Clientes c1')
    expect(at('SELECT * FROM Clientes c JOIN vendas.Clientes c1 JOIN |').items.find((i) => i.label === 'Clientes')?.insertText).toBe('Clientes c2')
  })

  it('alias que seria palavra reservada ganha número', () => {
    expect(at('SELECT * FROM |').items.find((i) => i.label === 'InscricaoNacional')?.insertText).toBe('InscricaoNacional in1')
  })

  it('não gera alias em UPDATE, INSERT INTO, DELETE FROM', () => {
    for (const s of ['UPDATE |', 'INSERT INTO |', 'DELETE FROM |'])
      expect(at(s).items.find((i) => i.label === 'Clientes')?.insertText).toBe('Clientes')
  })

  it('não gera alias quando o usuário já digitou um depois do nome', () => {
    expect(at('SELECT * FROM | c WHERE 1=1').items.find((i) => i.label === 'Clientes')?.insertText).toBe('Clientes')
    expect(at('SELECT * FROM | AS c').items.find((i) => i.label === 'Clientes')?.insertText).toBe('Clientes')
    // palavra-chave depois do cursor não é alias
    expect(at('SELECT * FROM | WHERE 1=1').items.find((i) => i.label === 'Clientes')?.insertText).toBe('Clientes c')
  })

  it('respeita a configuração desligada', () => {
    expect(at('SELECT * FROM |', off).items.find((i) => i.label === 'Clientes')?.insertText).toBe('Clientes')
  })

  it('schema como sugestão reabre a lista', () => {
    const s = at('SELECT * FROM |').items.find((i) => i.kind === 'schema')
    expect(s).toMatchObject({ label: 'vendas', insertText: 'vendas.', retrigger: true })
  })
})

describe('suggest: colunas', () => {
  it('alias. mostra as colunas da tabela, com o tipo', () => {
    const r = at('SELECT c.| FROM Clientes c')
    expect(r.items.map((i) => [i.label, i.description])).toEqual([['Id', 'int not null'], ['Nome', 'nvarchar(50)']])
  })
  it('Tabela. e schema.Tabela. também', () => {
    expect(labels(at('SELECT Clientes.| FROM Clientes'))).toEqual(['Id', 'Nome'])
    expect(labels(at('SELECT * FROM vendas.Pedidos WHERE vendas.Pedidos.|'))).toEqual(['Numero'])
  })
  it('schema. mostra os objetos do schema', () => {
    expect(labels(at('SELECT * FROM vendas.|')).sort()).toEqual(['Clientes', 'Pedidos'])
  })
  it('SELECT/WHERE: colunas das tabelas do statement mais funções e palavras-chave', () => {
    const r = at('SELECT | FROM Clientes c')
    const l = labels(r)
    expect(l).toEqual(expect.arrayContaining(['Id', 'Nome', 'COUNT', 'CASE']))
  })
  it('coluna ambígua leva o alias como prefixo', () => {
    const l = labels(at('SELECT | FROM Clientes c JOIN PedidoItens pi ON 1=1'))
    expect(l).toEqual(expect.arrayContaining(['c.Id', 'pi.Id', 'Nome', 'ClienteId', 'Qtd']))
    expect(l).not.toContain('Id')
    const ins = at('SELECT | FROM Clientes c JOIN PedidoItens pi ON 1=1').items.find((i) => i.label === 'pi.Id')
    expect(ins?.insertText).toBe('pi.Id')
  })
  it('sem alias, o prefixo é o nome da tabela', () => {
    const l = labels(at('SELECT | FROM Clientes JOIN PedidoItens ON 1=1'))
    expect(l).toEqual(expect.arrayContaining(['Clientes.Id', 'PedidoItens.Id']))
  })
  it('UPDATE ... SET sugere colunas da tabela do UPDATE', () => {
    expect(labels(at('UPDATE Clientes SET |'))).toEqual(expect.arrayContaining(['Id', 'Nome']))
  })
  it('colunas ainda não carregadas: não sugere colunas, mas o resto continua', () => {
    const partial = buildIndex({ ...dto, columnsLoaded: false, columns: {} })
    const l = labels(at('SELECT | FROM Clientes c', on, partial))
    expect(l).not.toContain('Nome')
    expect(l).toContain('SELECT'.length ? 'COUNT' : '')
  })
  it('nomes com espaço ficam entre colchetes', () => {
    const odd = buildIndex({
      ...dto,
      objects: [{ schema: 'dbo', name: 'Minha Tabela', type: 'table' }],
      columns: { 'dbo.Minha Tabela': [{ name: 'Nome Completo', type: 'varchar(10)', nullable: true }] },
    })
    expect(at('SELECT * FROM |', on, odd).items[0].insertText).toBe('[Minha Tabela] mt')
    expect(at('SELECT t.| FROM [Minha Tabela] t', on, odd).items[0].insertText).toBe('[Nome Completo]')
  })
})

describe('suggest: EXEC, outros e sem cache', () => {
  it('EXEC lista procedures', () => {
    expect(labels(at('EXEC |'))).toEqual(['pr_Limpa'])
  })
  it('início de statement: palavras-chave e snippets', () => {
    const r = at('|')
    expect(labels(r)).toEqual(expect.arrayContaining(['SELECT', 'sel', 'selt', 'tran']))
  })
  it('sem cache (conexão ainda não carregada), cai nas palavras-chave', () => {
    expect(labels(at('SELECT * FROM |', on, null as never))).toEqual([])
    expect(labels(suggest('SEL', 3, null, on))).toContain('SELECT')
  })
  it('não sugere dentro de comentário ou string', () => {
    expect(at("SELECT 'abc|'").items).toEqual([])
    expect(at('SELECT 1 -- nota|').items).toEqual([])
  })
})

describe('quoteIfNeeded e displayName', () => {
  it('coloca colchetes só quando preciso', () => {
    expect(quoteIfNeeded('Clientes')).toBe('Clientes')
    expect(quoteIfNeeded('Minha Tabela')).toBe('[Minha Tabela]')
    expect(quoteIfNeeded('Order')).toBe('[Order]')
    expect(quoteIfNeeded('a]b')).toBe('[a]]b]')
    expect(displayName({ schema: 'dbo', name: 'Clientes', type: 'table' })).toBe('Clientes')
    expect(displayName({ schema: 'vendas', name: 'Pedidos', type: 'table' })).toBe('vendas.Pedidos')
  })
})

describe('suggest: MySQL (sem nível de schema)', () => {
  const my = buildIndex({
    loaded: true, columnsLoaded: true, loading: false, hasSchemaLevel: false,
    schemas: ['outro', 'sqldesk_test'],
    objects: [
      { schema: 'sqldesk_test', name: 't', type: 'table' },
      { schema: 'outro', name: 't', type: 'table' },
      { schema: 'outro', name: 'u', type: 'view' },
      { schema: 'sqldesk_test', name: 'p', type: 'procedure' },
    ],
    columns: {},
  }, 'sqldesk_test')

  it('tabela do banco atual sai sem qualificação e a de outro banco qualificada', () => {
    const r = at('SELECT * FROM |', off, my)
    const t = r.items.find((i) => i.label === 't')!
    expect(t.insertText).toBe('t')
    expect(r.items.find((i) => i.label === 'outro.t')!.insertText).toBe('outro.t')
    expect(labels(r)).not.toContain('sqldesk_test.t')
  })

  it('oferece os outros bancos como primeiro nível, não o atual', () => {
    const dbs = at('SELECT * FROM |', off, my).items.filter((i) => i.kind === 'schema')
    expect(dbs.map((i) => i.label)).toEqual(['outro'])
    expect(dbs[0].description).toBe('banco')
  })

  it('depois de "outro." oferece as tabelas daquele banco', () => {
    expect(labels(at('SELECT * FROM outro.|', off, my)).sort()).toEqual(['t', 'u'])
  })

  it('procedure do banco atual sai sem qualificação no EXEC', () => {
    expect(labels(at('EXEC |', off, my))).toEqual(['p'])
  })

  it('SQL Server segue com dbo curto e demais schemas qualificados', () => {
    const l = labels(at('SELECT * FROM |', off))
    expect(l).toContain('Clientes')
    expect(l).toContain('vendas.Pedidos')
    expect(at('SELECT * FROM |', off).items.find((i) => i.label === 'vendas')!.description).toBe('schema')
  })
})

describe('MySQL: crases, comentários e separadores', () => {
  const my = { autoAlias: true, provider: 'mysql' as const }
  const myIndex = buildIndex({
    loaded: true, columnsLoaded: true, loading: false, hasSchemaLevel: false,
    schemas: ['loja', 'sqldesk_test'],
    objects: [
      { schema: 'sqldesk_test', name: 'clientes', type: 'table' },
      { schema: 'sqldesk_test', name: 'Minha Tabela', type: 'table' },
      { schema: 'sqldesk_test', name: 'order', type: 'table' },
      { schema: 'loja', name: 'pedidos', type: 'table' },
      { schema: 'sqldesk_test', name: 'limpa', type: 'procedure' },
    ],
    columns: {
      'sqldesk_test.clientes': [{ name: 'id', type: 'int', nullable: false }, { name: 'nome completo', type: 'varchar(50)', nullable: true }],
      'loja.pedidos': [{ name: 'numero', type: 'int', nullable: false }],
    },
  }, 'sqldesk_test')
  const atMy = (s: string, idx = myIndex) => at(s, my, idx)

  it('unquote tira as crases e desfaz a crase dobrada', () => {
    expect(unquote('`tabela`')).toBe('tabela')
    expect(unquote('`a``b`')).toBe('a`b')
  })

  it('parseTableRefs reconhece `schema`.`tabela` e `alias`', () => {
    expect(parseTableRefs('SELECT * FROM `loja`.`pedidos` `p` JOIN `sqldesk_test`.`Minha Tabela` AS `m` ON 1=1')).toEqual([
      { schema: 'loja', name: 'pedidos', alias: 'p' },
      { schema: 'sqldesk_test', name: 'Minha Tabela', alias: 'm' },
    ])
  })

  it('detectContext reconhece `alias`. como qualificado', () => {
    expect(detectContext('SELECT `p`.')).toEqual({ kind: 'qualified', parts: ['p'] })
    expect(detectContext('SELECT * FROM `loja`.')).toEqual({ kind: 'qualified', parts: ['loja'] })
  })

  it('scan mascara # e "-- " mas mantém as crases; strings com barra invertida não terminam antes da hora', () => {
    const s = "SELECT 1 # DROP x\nFROM `a b` -- DELETE\nWHERE c = 'it\\'s UPDATE'"
    const { masked } = scan(s, s.length, 'mysql')
    expect(masked.length).toBe(s.length)
    expect(masked).not.toMatch(/DROP|DELETE|UPDATE/)
    expect(masked).toContain('`a b`')
    expect(scan(s, s.length, 'mysql').inside).toBe(false)
    expect(scan('SELECT 1 # nota', 14, 'mysql').inside).toBe(true)
  })

  it('no SQL Server, # e crase continuam como antes', () => {
    expect(scan('SELECT #tmp', 11).masked).toBe('SELECT #tmp')
  })

  it('"--" sem espaço não é comentário no MySQL', () => {
    expect(scan('SELECT 1--1', 11, 'mysql').masked).toBe('SELECT 1--1')
  })

  it('no MySQL GO não separa statements, ; separa', () => {
    const b = (t: string, c: number) => {
      const r = statementBounds(t, c, 'mysql')
      return t.slice(r.start, r.end).trim()
    }
    expect(b('SELECT 1\nGO\nSELECT 2', 18)).toBe('SELECT 1\nGO\nSELECT 2')
    expect(b('SELECT 1; SELECT 2; SELECT 3', 14)).toBe('SELECT 2')
  })

  it('alias entre crases resolve as colunas da tabela', () => {
    expect(labels(atMy('SELECT `c`.| FROM `clientes` `c`'))).toEqual(['id', 'nome completo'])
    expect(labels(atMy('SELECT p.| FROM `loja`.`pedidos` p'))).toEqual(['numero'])
  })

  it('nomes que precisam de aspas saem entre crases (espaço ou palavra reservada do MySQL)', () => {
    const r = atMy('SELECT * FROM |')
    expect(r.items.find((i) => i.label === 'Minha Tabela')?.insertText).toBe('`Minha Tabela` mt')
    expect(r.items.find((i) => i.label === 'order')?.insertText).toBe('`order` o')
    expect(r.items.find((i) => i.label === 'clientes')?.insertText).toBe('clientes c')
    expect(atMy('SELECT c.| FROM clientes c').items.find((i) => i.label === 'nome completo')?.insertText).toBe('`nome completo`')
  })

  it('palavra reservada só do MySQL (LIMIT) ganha crases; no SQL Server não', () => {
    expect(quoteIfNeeded('limit', 'mysql')).toBe('`limit`')
    expect(quoteIfNeeded('limit')).toBe('limit')
    expect(quoteIfNeeded('a`b', 'mysql')).toBe('`a``b`')
  })

  it('CALL lista as procedures no MySQL', () => {
    expect(labels(atMy('CALL |'))).toEqual(['limpa'])
  })

  it('comentário # não gera sugestão', () => {
    expect(atMy('SELECT 1 # nota|').items).toEqual([])
  })
})
