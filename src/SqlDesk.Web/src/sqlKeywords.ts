export const KEYWORDS: readonly string[] = [
  'SELECT', 'FROM', 'WHERE', 'GROUP BY', 'ORDER BY', 'HAVING', 'JOIN', 'INNER JOIN', 'LEFT JOIN', 'RIGHT JOIN', 'FULL JOIN', 'CROSS JOIN',
  'ON', 'AS', 'AND', 'OR', 'NOT', 'IN', 'EXISTS', 'BETWEEN', 'LIKE', 'IS NULL', 'IS NOT NULL', 'DISTINCT', 'TOP', 'UNION', 'UNION ALL',
  'EXCEPT', 'INTERSECT', 'CASE', 'WHEN', 'THEN', 'ELSE', 'END', 'ASC', 'DESC', 'OFFSET', 'FETCH NEXT', 'WITH', 'NOLOCK',
  'INSERT INTO', 'VALUES', 'UPDATE', 'SET', 'DELETE FROM', 'MERGE', 'USING', 'OUTPUT', 'TRUNCATE TABLE',
  'CREATE TABLE', 'ALTER TABLE', 'DROP TABLE', 'CREATE VIEW', 'CREATE PROCEDURE', 'CREATE INDEX', 'PRIMARY KEY', 'FOREIGN KEY', 'REFERENCES',
  'DEFAULT', 'NULL', 'IDENTITY', 'CONSTRAINT', 'DECLARE', 'BEGIN', 'BEGIN TRAN', 'COMMIT', 'ROLLBACK', 'TRANSACTION', 'IF', 'WHILE',
  'RETURN', 'EXEC', 'PRINT', 'GO', 'USE', 'OVER', 'PARTITION BY', 'ROWS', 'TRY', 'CATCH', 'THROW', 'RAISERROR',
]

export const FUNCTIONS: readonly string[] = [
  'COUNT', 'SUM', 'AVG', 'MIN', 'MAX', 'STRING_AGG', 'ROW_NUMBER', 'RANK', 'DENSE_RANK', 'LAG', 'LEAD', 'NTILE',
  'GETDATE', 'SYSDATETIME', 'GETUTCDATE', 'DATEADD', 'DATEDIFF', 'DATEPART', 'DATENAME', 'EOMONTH', 'YEAR', 'MONTH', 'DAY', 'FORMAT',
  'ISNULL', 'COALESCE', 'NULLIF', 'IIF', 'CAST', 'CONVERT', 'TRY_CAST', 'TRY_CONVERT',
  'LEN', 'LTRIM', 'RTRIM', 'TRIM', 'UPPER', 'LOWER', 'SUBSTRING', 'REPLACE', 'CHARINDEX', 'CONCAT', 'LEFT', 'RIGHT', 'STUFF', 'STRING_SPLIT', 'REPLICATE',
  'ABS', 'ROUND', 'CEILING', 'FLOOR', 'POWER', 'SQRT', 'RAND', 'NEWID', 'SCOPE_IDENTITY', 'OBJECT_ID', 'DB_NAME', 'SUSER_NAME', 'ISNUMERIC', 'ISDATE',
]

export interface SnippetDef {
  trigger: string
  description: string
  body: string
}

export const SNIPPETS: readonly SnippetDef[] = [
  { trigger: 'sel', description: 'SELECT * FROM', body: 'SELECT * FROM ${1:tabela}' },
  { trigger: 'selt', description: 'SELECT TOP 100 * FROM', body: 'SELECT TOP 100 * FROM ${1:tabela}' },
  { trigger: 'selc', description: 'SELECT COUNT(*) FROM', body: 'SELECT COUNT(*) FROM ${1:tabela}' },
  { trigger: 'upd', description: 'UPDATE com WHERE', body: 'UPDATE ${1:tabela} SET ${2:coluna} = ${3:valor} WHERE ${4:condicao}' },
  { trigger: 'del', description: 'DELETE com WHERE', body: 'DELETE FROM ${1:tabela} WHERE ${2:condicao}' },
  { trigger: 'ins', description: 'INSERT INTO ... VALUES', body: 'INSERT INTO ${1:tabela} (${2:colunas}) VALUES (${3:valores})' },
  { trigger: 'tran', description: 'Bloco de transação', body: 'BEGIN TRAN;\n${0}\n-- COMMIT;\n-- ROLLBACK;' },
]
