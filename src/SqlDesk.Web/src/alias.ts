/** Palavras reservadas do T-SQL: um alias não pode ser uma delas sem colchetes. */
export const RESERVED_WORDS: ReadonlySet<string> = new Set(
  `ADD ALL ALTER AND ANY AS ASC AUTHORIZATION BACKUP BEGIN BETWEEN BREAK BROWSE BULK BY CASCADE CASE CHECK CHECKPOINT CLOSE CLUSTERED
   COALESCE COLLATE COLUMN COMMIT COMPUTE CONSTRAINT CONTAINS CONTAINSTABLE CONTINUE CONVERT CREATE CROSS CURRENT CURSOR DATABASE DBCC
   DEALLOCATE DECLARE DEFAULT DELETE DENY DESC DISK DISTINCT DISTRIBUTED DOUBLE DROP DUMP ELSE END ERRLVL ESCAPE EXCEPT EXEC EXECUTE
   EXISTS EXIT EXTERNAL FETCH FILE FILLFACTOR FOR FOREIGN FREETEXT FREETEXTTABLE FROM FULL FUNCTION GO GOTO GRANT GROUP HAVING HOLDLOCK
   IDENTITY IDENTITY_INSERT IDENTITYCOL IF IN INDEX INNER INSERT INTERSECT INTO IS JOIN KEY KILL LEFT LIKE LINENO LOAD MERGE NATIONAL
   NOCHECK NONCLUSTERED NOT NULL NULLIF OF OFF OFFSETS ON OPEN OPENDATASOURCE OPENQUERY OPENROWSET OPENXML OPTION OR ORDER OUTER OVER
   PERCENT PIVOT PLAN PRECISION PRIMARY PRINT PROC PROCEDURE PUBLIC RAISERROR READ READTEXT RECONFIGURE REFERENCES REPLICATION RESTORE
   RESTRICT RETURN REVERT REVOKE RIGHT ROLLBACK ROWCOUNT ROWGUIDCOL RULE SAVE SCHEMA SECURITYAUDIT SELECT SEMANTICKEYPHRASETABLE
   SEMANTICSIMILARITYDETAILSTABLE SEMANTICSIMILARITYTABLE SESSION_USER SET SETUSER SHUTDOWN SOME STATISTICS SYSTEM_USER TABLE
   TABLESAMPLE TEXTSIZE THEN TO TOP TRAN TRANSACTION TRIGGER TRUNCATE TRY_CONVERT TSEQUAL UNION UNIQUE UNPIVOT UPDATE UPDATETEXT USE
   USER VALUES VARYING VIEW WAITFOR WHEN WHERE WHILE WITH WITHIN WRITETEXT`.split(/\s+/),
)

/** Tira colchetes/aspas de um identificador: `[Minha Tabela]` vira `Minha Tabela`. */
export function unquote(identifier: string): string {
  const m = /^\[(.*)\]$/.exec(identifier) ?? /^"(.*)"$/.exec(identifier)
  return m ? m[1].replace(/\]\]/g, ']') : identifier
}

/** A base do alias, antes de resolver conflitos e palavras reservadas. */
function baseAlias(tableName: string): string {
  const name = unquote(tableName)

  // Com "_": a primeira letra de cada parte (pedido_itens vira pi).
  if (name.includes('_')) {
    const letters = name.split('_').map((p) => /[A-Za-zÀ-ÿ]/.exec(p)?.[0] ?? '').join('')
    if (letters) return letters.toLowerCase()
  }

  // Misto (PascalCase ou camelCase): a primeira letra mais as maiúsculas seguintes (ComunicacaoProcessual vira cp).
  const letters = [...name].filter((ch) => /[A-Za-zÀ-ÿ]/.test(ch))
  if (letters.length === 0) return 't'
  const hasLower = letters.some((ch) => ch !== ch.toUpperCase())
  const hasUpper = letters.some((ch) => ch !== ch.toLowerCase())
  if (hasLower && hasUpper) {
    const caps = letters.slice(1).filter((ch) => ch !== ch.toLowerCase())
    return (letters[0] + caps.join('')).toLowerCase()
  }

  // Nome simples (clientes, CLIENTES): a primeira letra.
  return letters[0].toLowerCase()
}

/**
 * Alias de tabela no estilo do DBeaver. Em conflito com um alias já usado no statement, ou se o resultado for
 * palavra reservada, acrescenta um número (c, c1, c2...; in vira in1).
 */
export function generateAlias(tableName: string, usedAliases: Iterable<string> = []): string {
  const used = new Set([...usedAliases].map((a) => a.toLowerCase()))
  const base = baseAlias(tableName)
  const ok = (a: string) => !used.has(a) && !RESERVED_WORDS.has(a.toUpperCase())
  if (ok(base)) return base
  for (let n = 1; ; n++) {
    const candidate = `${base}${n}`
    if (ok(candidate)) return candidate
  }
}
