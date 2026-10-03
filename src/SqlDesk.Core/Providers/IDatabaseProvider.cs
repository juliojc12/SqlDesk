using System.Data.Common;
using SqlDesk.Core.Connections;
using SqlDesk.SqlAnalysis;

namespace SqlDesk.Core.Providers;

public sealed record ConnectionFailure(string Message, bool CertificateUntrusted);

/// <summary>Tudo o que muda de um banco para outro: conexão, erros, transações e catálogo.</summary>
public interface IDatabaseProvider
{
    string Id { get; }
    string DisplayName { get; }
    int DefaultPort { get; }
    ISqlAnalyzer Analyzer { get; }
    /// <summary>true: DDL (DROP/TRUNCATE/ALTER) confirma a transação sozinho e não dá para desfazer.</summary>
    bool DdlCommitsImplicitly { get; }

    string BuildConnectionString(ConnectionSettings settings, string? password);
    (ConnectionSettings Settings, string? Password) ParseConnectionString(string connectionString);
    DbConnection CreateConnection(string connectionString);
    ConnectionFailure Translate(DbException ex);

    /// <summary>Recebe mensagens informativas do servidor (PRINT / avisos). Dispose cancela a inscrição.</summary>
    IDisposable SubscribeInfoMessages(DbConnection connection, Action<string> onMessage);
    /// <summary>
    /// Liga o aviso "N linhas afetadas" por statement. Devolve false se o driver não tem esse evento
    /// (o QueryRunner então usa <c>DbDataReader.RecordsAffected</c>).
    /// </summary>
    bool TryAttachStatementCompleted(DbCommand command, Action<long> onCompleted);
    /// <summary>Número do erro do servidor (para o erro 334 etc.), ou null.</summary>
    int? ErrorNumber(DbException ex);
    IEnumerable<(string Message, int? Line, bool IsError)> ErrorDetails(DbException ex, int batchFirstLine);

    ITransactionSql Transactions { get; }
    IMetadataSql Metadata { get; }
}

public interface ITransactionSql
{
    /// <summary>Consulta que devolve quantas transações estão abertas (0 = nenhuma). null = o provedor não consegue consultar.</summary>
    string? OpenCountSql { get; }
    string BeginSql { get; }
    string CommitAllSql { get; }
    string RollbackAllSql { get; }
    string SavepointSql(string name);
    string RollbackToSavepointSql(string name);
    /// <summary>
    /// Libera um savepoint. null = o banco não usa a marca de transação (SQL Server). Nos bancos que confirmam DDL
    /// sozinhos, a trava cria um savepoint-marca e o libera no fim: se ele sumiu, a transação da trava acabou no meio.
    /// </summary>
    string? ReleaseSavepointSql(string name);
    /// <summary>Número do erro "savepoint não existe" (MySQL/MariaDB: 1305), ou null.</summary>
    int? SavepointMissingError { get; }
    string CountRowsSql(string quotedTable);

    /// <summary>
    /// Consulta parametrizada (<c>@schema</c>, que pode ser nulo = banco atual, e <c>@name</c>) que devolve
    /// TABLE_SCHEMA, TABLE_NAME e ENGINE de uma tabela. null = o banco não tem tabela fora de transação (SQL Server).
    /// </summary>
    string? TableEngineSql => null;

    /// <summary>O mecanismo guarda as alterações numa transação (null = view, que não tem mecanismo próprio).</summary>
    bool IsTransactionalEngine(string? engine) => true;

    /// <summary>Código do aviso "ROLLBACK não desfez tabelas não transacionais" (MySQL/MariaDB: 1196), ou null.</summary>
    int? RollbackIncompleteWarning => null;
}

public interface IMetadataSql
{
    string ObjectsSql { get; }
    string ColumnsSql { get; }
    /// <summary>Converte o tipo de objeto devolvido pela consulta para "table"|"view"|"procedure"|"function".</summary>
    string ObjectKind(string rawType);
    /// <summary>Lê uma linha da consulta de colunas para o tipo no formato de exibição.</summary>
    string FormatType(DbDataReader row);
    bool HasSchemaLevel { get; }          // SQL Server: true; MySQL: false (banco == schema)
}
