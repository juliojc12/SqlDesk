import { describe, expect, it } from 'vitest'
import type { ConnectionSettings } from './contracts'
import { labelParts, PROVIDERS, providerOf, withProvider } from './providers'

const base: ConnectionSettings = {
  server: 'srv01,1450', database: 'Vendas', user: 'joao', connectTimeout: 20, commandTimeout: 90,
  encrypt: false, trustServerCertificate: true, advanced: { ApplicationName: 'x' },
}

describe('providers', () => {
  it('quote: crases no MySQL e colchetes no SQL Server, com o escape de cada um', () => {
    expect(PROVIDERS.mysql.quote('a`b')).toBe('`a``b`')
    expect(PROVIDERS.sqlserver.quote('a]b')).toBe('[a]]b]')
    expect(PROVIDERS.mysql.quote('Minha Tabela')).toBe('`Minha Tabela`')
  })

  it('rótulos de criptografia e certificado do MySQL', () => {
    expect(PROVIDERS.mysql.encryptLabel).toBe('Criptografar a conexão (SSL)')
    expect(PROVIDERS.mysql.trustLabel).toBe('Aceitar certificado não verificado (SslMode=Required)')
    expect(PROVIDERS.mysql.defaultPort).toBe(3306)
    expect(PROVIDERS.sqlserver.defaultPort).toBe(1433)
  })

  it('providerOf: sem provider (conexão salva antes do MySQL) vale SQL Server', () => {
    expect(providerOf({}).id).toBe('sqlserver')
    expect(providerOf({ provider: undefined }).id).toBe('sqlserver')
    expect(providerOf({ provider: 'mysql' }).id).toBe('mysql')
    expect(providerOf({ provider: 'outro' }).id).toBe('sqlserver')
  })

  it('withProvider zera as opções avançadas, mantém usuário, banco e timeouts e volta ao padrão seguro de criptografia', () => {
    const s = withProvider(base, 'mysql')
    expect(s.provider).toBe('mysql')
    expect(s.advanced).toEqual({})
    expect(s.user).toBe('joao')
    expect(s.database).toBe('Vendas')
    expect(s.connectTimeout).toBe(20)
    expect(s.commandTimeout).toBe(90)
    expect(s.encrypt).toBe(true)
    expect(s.trustServerCertificate).toBe(false)
  })

  it('withProvider converte a porta do servidor para a sintaxe do outro banco', () => {
    expect(withProvider(base, 'mysql').server).toBe('srv01:1450')
    expect(withProvider({ ...base, server: 'srv01,1433' }, 'mysql').server).toBe('srv01')
    expect(withProvider({ ...base, provider: 'mysql', server: 'db:3307' }, 'sqlserver').server).toBe('db,3307')
    expect(withProvider({ ...base, provider: 'mysql', server: 'db:3306' }, 'sqlserver').server).toBe('db')
    expect(withProvider({ ...base, provider: 'mysql', server: '[::1]:3307' }, 'sqlserver').server).toBe('[::1],3307')
    expect(withProvider({ ...base, server: '' }, 'mysql').server).toBe('')
    expect(withProvider({ ...base, server: 'srv01' }, 'mysql').server).toBe('srv01')
  })

  it('withProvider para o mesmo banco não muda nada', () => {
    const mysql = { ...base, provider: 'mysql' as const }
    expect(withProvider(mysql, 'mysql')).toBe(mysql)
    expect(withProvider(base, 'sqlserver')).toBe(base)
  })
})

describe('labelParts', () => {
  it('separa o trecho final entre parênteses', () => {
    expect(labelParts('Criptografar a conexão (SSL)')).toEqual(['Criptografar a conexão', '(SSL)'])
    expect(labelParts('Aceitar certificado não verificado (SslMode=Required)')).toEqual(['Aceitar certificado não verificado', '(SslMode=Required)'])
    expect(labelParts('Sem dica')).toEqual(['Sem dica', ''])
  })
})
