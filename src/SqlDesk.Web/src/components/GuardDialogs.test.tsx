import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { describeChange } from '../guardDiff'
import { DangerDialog } from './GuardDialogs'

const blocked = [{ line: 1, description: 'DROP TABLE g1 apaga a tabela e os dados dela' }]
const noop = () => {}
const text = (html: string) => html.replace(/<[^>]+>/g, '').replace(/\s+/g, ' ')

describe('DangerDialog', () => {
  it('sem irreversible, o texto e o botão continuam os de sempre', () => {
    const html = renderToStaticMarkup(<DangerDialog blocked={blocked} onCancel={noop} onContinue={noop} />)
    expect(text(html)).toContain('Se continuar, o texto roda dentro de uma transação e você verá o resultado antes de decidir entre Commit e Rollback.')
    expect(html).toContain('>Continuar</button>')
    expect(html).not.toContain('irreversible-warning')
    expect(html).not.toContain('Executar mesmo assim')
  })

  it('com irreversible, avisa que não há volta e o botão vira "Executar mesmo assim"', () => {
    const html = renderToStaticMarkup(<DangerDialog blocked={blocked} irreversible onCancel={noop} onContinue={noop} />)
    expect(html).toContain('irreversible-warning')
    expect(html).toContain('<strong>não pode ser desfeito</strong>')
    expect(text(html)).toContain(
      'Este comando muda a estrutura do banco ou grava de forma definitiva e não pode ser desfeito: o MySQL/MariaDB confirma sozinho mudanças de estrutura, e tabelas sem transação (como MyISAM) não voltam com Rollback. Não haverá Commit/Rollback depois.',
    )
    expect(html).toContain('>Executar mesmo assim</button>')
    expect(html).not.toContain('>Continuar</button>')
    expect(text(html)).not.toContain('roda dentro de uma transação')
  })
})

describe('describeChange: alterTable', () => {
  it('usa a descrição do backend (ou "ALTER TABLE em alvo" sem ela)', () => {
    const c = { kind: 'alterTable' as const, target: 't', line: 1, description: 'ALTER TABLE t muda a estrutura do banco', hasPreview: false, columns: [], before: [], after: [] }
    expect(describeChange(c)).toBe('ALTER TABLE t muda a estrutura do banco')
    expect(describeChange({ ...c, description: '' })).toBe('ALTER TABLE em t')
  })
})
