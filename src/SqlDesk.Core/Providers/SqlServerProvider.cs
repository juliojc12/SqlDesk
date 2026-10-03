using System.Data.Common;
using Microsoft.Data.SqlClient;
using SqlDesk.Core.Connections;
using SqlDesk.Core.Metadata;
using SqlDesk.SqlAnalysis;

namespace SqlDesk.Core.Providers;

/// <summary>Provedor do SQL Server: só embrulha o código que já existia.</summary>
public sealed class SqlServerProvider : IDatabaseProvider
{
    public string Id => ProviderIds.SqlServer;
    public string DisplayName => "SQL Server";
    public int DefaultPort => 1433;
    public ISqlAnalyzer Analyzer => SqlServerAnalyzer.Instance;
    public bool DdlCommitsImplicitly => false;

    public ITransactionSql Transactions { get; } = new SqlServerTransactionSql();
    public IMetadataSql Metadata { get; } = new SqlServerMetadataSql();

    public string BuildConnectionString(ConnectionSettings settings, string? password) =>
        ConnectionStringService.Build(settings, password);

    public (ConnectionSettings Settings, string? Password) ParseConnectionString(string connectionString) =>
        ConnectionStringService.Parse(connectionString);

    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    public ConnectionFailure Translate(DbException ex) =>
        ex is SqlException s
            ? new ConnectionFailure(SqlErrorTranslator.Translate(s), SqlErrorTranslator.IsCertificateError(s))
            : new ConnectionFailure(ex.Message, false);

    public IDisposable SubscribeInfoMessages(DbConnection connection, Action<string> onMessage)
    {
        var sql = (SqlConnection)connection;
        SqlInfoMessageEventHandler handler = (_, e) =>
        {
            foreach (SqlError err in e.Errors) onMessage(err.Message);
        };
        sql.InfoMessage += handler;
        return new Unsubscribe(() => sql.InfoMessage -= handler);
    }

    public bool TryAttachStatementCompleted(DbCommand command, Action<long> onCompleted)
    {
        if (command is not SqlCommand sc) return false;
        sc.StatementCompleted += (_, e) => { if (e.RecordCount >= 0) onCompleted(e.RecordCount); };
        return true;
    }

    public int? ErrorNumber(DbException ex) => (ex as SqlException)?.Number;

    public IEnumerable<(string Message, int? Line, bool IsError)> ErrorDetails(DbException ex, int batchFirstLine)
    {
        if (ex is not SqlException s) yield break;
        foreach (SqlError err in s.Errors)
        {
            // Dentro de procedure a linha é relativa a ela, não ao documento.
            int? line = string.IsNullOrEmpty(err.Procedure) && err.LineNumber > 0 ? batchFirstLine + err.LineNumber - 1 : null;
            yield return (err.Message, line, err.Class > 10 || s.Errors.Count == 1);
        }
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private sealed class SqlServerTransactionSql : ITransactionSql
    {
        public string? OpenCountSql => "SELECT @@TRANCOUNT";
        public string BeginSql => "BEGIN TRANSACTION";
        public string CommitAllSql => "WHILE @@TRANCOUNT > 0 COMMIT TRANSACTION";
        public string RollbackAllSql => "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION";
        public string SavepointSql(string name) => $"SAVE TRANSACTION {name}";
        public string RollbackToSavepointSql(string name) => $"ROLLBACK TRANSACTION {name}";
        public string? ReleaseSavepointSql(string name) => null; // a transação não some sem o @@TRANCOUNT perceber
        public int? SavepointMissingError => null;
        public string CountRowsSql(string quotedTable) => $"SELECT COUNT_BIG(*) FROM {quotedTable}";
    }

    private sealed class SqlServerMetadataSql : IMetadataSql
    {
        public string ObjectsSql => MetadataQueries.Objects;
        public string ColumnsSql => MetadataQueries.Columns;
        public string ObjectKind(string rawType) => MetadataReader.ObjectKind(rawType);
        public string FormatType(DbDataReader row) =>
            MetadataReader.FormatType(row.GetString(3), Convert.ToInt32(row.GetValue(4)), Convert.ToInt32(row.GetValue(5)), Convert.ToInt32(row.GetValue(6)));
        public bool HasSchemaLevel => true;
    }
}
