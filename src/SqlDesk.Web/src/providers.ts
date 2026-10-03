import { RESERVED_WORDS } from './alias'
import type { ConnectionSettings } from './contracts'

/** Banco de uma conexão. Espelha SqlDesk.Core.Providers.ProviderIds. */
export type ProviderId = 'sqlserver' | 'mysql'

export interface ProviderInfo {
  id: ProviderId
  /** Nome curto, para a lista de conexões e a barra de status. */
  name: string
  defaultPort: number
  /** Rótulo da opção `encrypt`; o trecho final entre parênteses aparece em cinza. */
  encryptLabel: string
  /** Rótulo da opção `trustServerCertificate`; o trecho final entre parênteses aparece em cinza. */
  trustLabel: string
  /** Põe o nome entre os delimitadores do banco, sempre (para "só se preciso", ver `quoteIfNeeded`). */
  quote(ident: string): string
}

export const PROVIDERS: Record<ProviderId, ProviderInfo> = {
  sqlserver: {
    id: 'sqlserver',
    name: 'SQL Server',
    defaultPort: 1433,
    encryptLabel: 'Criptografar a conexão (Encrypt)',
    trustLabel: 'Confiar no certificado do servidor (TrustServerCertificate)',
    quote: (ident) => `[${ident.replace(/]/g, ']]')}]`,
  },
  mysql: {
    id: 'mysql',
    name: 'MySQL',
    defaultPort: 3306,
    encryptLabel: 'Criptografar a conexão (SSL)',
    trustLabel: 'Aceitar certificado não verificado (SslMode=Required)',
    quote: (ident) => `\`${ident.replace(/`/g, '``')}\``,
  },
}

/** Rótulos do seletor "Tipo de servidor". */
export const PROVIDER_CHOICES: { id: ProviderId; label: string }[] = [
  { id: 'sqlserver', label: 'SQL Server' },
  { id: 'mysql', label: 'MySQL / MariaDB' },
]

/** Provedor das configurações. Sem `provider` (conexão salva antes do MySQL) ou com valor desconhecido: SQL Server. */
export const providerOf = (s: { provider?: string } | null | undefined): ProviderInfo =>
  s?.provider === 'mysql' ? PROVIDERS.mysql : PROVIDERS.sqlserver

/** Troca a sintaxe da porta no servidor: `host,porta` (SQL Server) ↔ `host:porta` (MySQL); a porta padrão do banco de origem some. */
function convertServer(server: string, from: ProviderId, to: ProviderId): string {
  const text = server.trim()
  if (from === 'sqlserver') {
    const m = /^(.+?)\s*,\s*(\d+)$/.exec(text)
    if (!m) return server
    return Number(m[2]) === PROVIDERS.sqlserver.defaultPort ? m[1] : `${m[1]}:${m[2]}`
  }
  // MySQL: "host:porta" ou "[ipv6]:porta"; IPv6 sem colchetes (vários ":") não tem porta.
  const m = /^(\[[^\]]*\]|[^:[\]]+):(\d+)$/.exec(text)
  if (!m || to !== 'sqlserver') return server
  return Number(m[2]) === PROVIDERS.mysql.defaultPort ? m[1] : `${m[1]},${m[2]}`
}

/**
 * Troca o banco das configurações: as opções avançadas são do driver anterior e saem; criptografia volta ao padrão seguro
 * (criptografar e validar o certificado); servidor, banco, usuário e timeouts ficam (a porta muda de sintaxe).
 */
export function withProvider(s: ConnectionSettings, id: ProviderId): ConnectionSettings {
  const from = providerOf(s).id
  if (from === id) return s
  return {
    ...s,
    provider: id,
    server: convertServer(s.server, from, id),
    encrypt: true,
    trustServerCertificate: false,
    advanced: {},
  }
}

/** Palavras reservadas do MySQL 8 / MariaDB que mais aparecem como nome de tabela ou coluna: exigem crases. */
export const MYSQL_RESERVED_WORDS: ReadonlySet<string> = new Set(
  `ACCESSIBLE ADD ALL ALTER ANALYZE AND AS ASC ASENSITIVE BEFORE BETWEEN BIGINT BINARY BLOB BOTH BY CALL CASCADE CASE CHANGE CHAR
   CHARACTER CHECK COLLATE COLUMN CONDITION CONSTRAINT CONTINUE CONVERT CREATE CROSS CUBE CUME_DIST CURRENT_DATE CURRENT_TIME
   CURRENT_TIMESTAMP CURRENT_USER CURSOR DATABASE DATABASES DAY_HOUR DAY_MICROSECOND DAY_MINUTE DAY_SECOND DEC DECIMAL DECLARE
   DEFAULT DELAYED DELETE DENSE_RANK DESC DESCRIBE DETERMINISTIC DISTINCT DISTINCTROW DIV DOUBLE DROP DUAL EACH ELSE ELSEIF EMPTY
   ENCLOSED ESCAPED EXCEPT EXISTS EXIT EXPLAIN FALSE FETCH FIRST_VALUE FLOAT FLOAT4 FLOAT8 FOR FORCE FOREIGN FROM FULLTEXT FUNCTION
   GENERATED GET GRANT GROUP GROUPING GROUPS HAVING HIGH_PRIORITY HOUR_MICROSECOND HOUR_MINUTE HOUR_SECOND IF IGNORE IN INDEX
   INFILE INNER INOUT INSENSITIVE INSERT INT INT1 INT2 INT3 INT4 INT8 INTEGER INTERSECT INTERVAL INTO IO_AFTER_GTIDS
   IO_BEFORE_GTIDS IS ITERATE JOIN JSON_TABLE KEY KEYS KILL LAG LAST_VALUE LATERAL LEAD LEADING LEAVE LEFT LIKE LIMIT LINEAR LINES
   LOAD LOCALTIME LOCALTIMESTAMP LOCK LONG LONGBLOB LONGTEXT LOOP LOW_PRIORITY MASTER_BIND MASTER_SSL_VERIFY_SERVER_CERT MATCH
   MAXVALUE MEDIUMBLOB MEDIUMINT MEDIUMTEXT MIDDLEINT MINUTE_MICROSECOND MINUTE_SECOND MOD MODIFIES NATURAL NOT NO_WRITE_TO_BINLOG
   NTH_VALUE NTILE NULL NUMERIC OF ON OPTIMIZE OPTIMIZER_COSTS OPTION OPTIONALLY OR ORDER OUT OUTER OUTFILE OVER PARTITION
   PERCENT_RANK PRECISION PRIMARY PROCEDURE PURGE RANGE RANK READ READS READ_WRITE REAL RECURSIVE REFERENCES REGEXP RELEASE RENAME
   REPEAT REPLACE REQUIRE RESIGNAL RESTRICT RETURN RETURNING REVOKE RIGHT RLIKE ROW ROWS ROW_NUMBER SCHEMA SCHEMAS SECOND_MICROSECOND
   SELECT SENSITIVE SEPARATOR SET SHOW SIGNAL SMALLINT SPATIAL SPECIFIC SQL SQLEXCEPTION SQLSTATE SQLWARNING SQL_BIG_RESULT
   SQL_CALC_FOUND_ROWS SQL_SMALL_RESULT SSL STARTING STORED STRAIGHT_JOIN SYSTEM TABLE TERMINATED THEN TINYBLOB TINYINT TINYTEXT TO
   TRAILING TRIGGER TRUE UNDO UNION UNIQUE UNLOCK UNSIGNED UPDATE USAGE USE USING UTC_DATE UTC_TIME UTC_TIMESTAMP VALUES VARBINARY
   VARCHAR VARCHARACTER VARYING VIRTUAL WHEN WHERE WHILE WINDOW WITH WRITE XOR YEAR_MONTH ZEROFILL`.split(/\s+/),
)

const MYSQL_ALIAS_RESERVED: ReadonlySet<string> = new Set([...RESERVED_WORDS, ...MYSQL_RESERVED_WORDS])

/**
 * Palavras que nunca são lidas como alias depois do nome de uma tabela (WHERE, JOIN, LIMIT...) nem geradas como alias.
 * No MySQL junta as do T-SQL com as do MySQL: errar para o lado de "não é alias" só deixa de reconhecer um alias raro.
 */
export const aliasReservedWords = (id: ProviderId = 'sqlserver'): ReadonlySet<string> => (id === 'mysql' ? MYSQL_ALIAS_RESERVED : RESERVED_WORDS)

/** Separa o rótulo do trecho final entre parênteses, que a interface mostra em cinza: "Criptografar (SSL)" → ["Criptografar", "(SSL)"]. */
export function labelParts(label: string): [string, string] {
  const m = /^(.*?)\s*(\([^()]*\))$/.exec(label)
  return m ? [m[1], m[2]] : [label, '']
}
