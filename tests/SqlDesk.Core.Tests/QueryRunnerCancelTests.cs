using System.Data;
using System.Data.Common;
using System.Reflection;
using SqlDesk.Core.Connections;
using SqlDesk.Core.Execution;
using SqlDesk.Core.Providers;
using SqlDesk.Core.Sessions;
using SqlDesk.SqlAnalysis;

namespace SqlDesk.Core.Tests;

/// <summary>QueryRunner e o cancelamento, com conexão e provedor falsos (sem banco).</summary>
public class QueryRunnerCancelTests
{
    private sealed class FakeProtector : IPasswordProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class DriverFailure(string message) : DbException(message);

    private sealed class FakeConnection(Func<DbDataReader> onExecute) : DbConnection
    {
        public Func<DbDataReader> OnExecute { get; } = onExecute;
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "d";
        public override string DataSource => "s";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new FakeCommand(this);
    }

    private sealed class FakeCommand(FakeConnection conn) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = conn;
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => conn.OnExecute();
    }

    /// <summary>Provedor que só responde ao que o QueryRunner usa; o resto lança.</summary>
    public class FakeProvider : DispatchProxy
    {
        private sealed class Nop : IDisposable { public void Dispose() { } }

        public static IDatabaseProvider Create() => Create<IDatabaseProvider, FakeProvider>();

        protected override object? Invoke(MethodInfo? m, object?[]? args) => m!.Name switch
        {
            "SubscribeInfoMessages" => new Nop(),
            "TryAttachStatementCompleted" => false,
            "ErrorNumber" => null,
            "ErrorDetails" => Array.Empty<(string, int?, bool)>(),
            _ => throw new NotSupportedException(m.Name),
        };
    }

    private sealed class Sink : IExecutionSink
    {
        public List<(string Kind, string Text)> Messages { get; } = [];
        public void ResultStarted(int resultIndex, DocRange source, IReadOnlyList<ColumnInfo> columns) { }
        public void Rows(int resultIndex, IReadOnlyList<object?[]> rows) { }
        public void ResultCompleted(int resultIndex, long rowCount, bool truncated) { }
        public void Message(string kind, string text, int? line) => Messages.Add((kind, text));
    }

    /// <summary>
    /// Leitor que, como o do MySQL depois do KILL QUERY, termina sem erro mesmo com o cancelamento pedido: não olha o token
    /// em NextResultAsync (o leitor do DataTable lançaria, e o teste cairia no caminho da exceção).
    /// </summary>
    private sealed class QuietReader(DbDataReader inner, Action? afterRows) : DbDataReader
    {
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override System.Collections.IEnumerator GetEnumerator() => inner.GetEnumerator();
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override bool NextResult() => inner.NextResult();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) { afterRows?.Invoke(); return Task.FromResult(inner.NextResult()); }
        public override bool Read() => inner.Read();
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(inner.Read());
        public override DataTable? GetSchemaTable() => inner.GetSchemaTable();
        public override void Close() => inner.Close();
    }

    private static DbDataReader OneRow(Action? afterRows = null)
    {
        var t = new DataTable();
        t.Columns.Add("a", typeof(int));
        t.Rows.Add(1);
        return new QuietReader(t.CreateDataReader(), afterRows);
    }

    /// <summary>Monta o runner com uma conexão falsa; <paramref name="onExecute"/> recebe o runner para poder cancelar durante a execução.</summary>
    private static async Task<(RunSummary Summary, Sink Sink)> RunAsync(Func<QueryRunner, DbDataReader> onExecute)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var sessions = new TabSessionManager(new ConnectionStore(Path.Combine(dir, "c.json"), new FakeProtector()));
            var runner = new QueryRunner(sessions);
            sessions.AttachForTests("aba", new FakeConnection(() => onExecute(runner)), FakeProvider.Create());
            var sink = new Sink();
            var summary = await runner.RunAsync("aba", BatchSplitter.Split("SELECT 1"), 0, 1, null, sink);
            return (summary, sink);
        }
        finally { Directory.Delete(dir, true); }
    }

    private const string GenuineCancel = "Execução cancelada pelo usuário.";
    private const string CancelAfterFinish =
        "Cancelamento pedido, mas o comando já tinha terminado: confira os dados (as alterações podem ter sido gravadas).";

    private static int CancelMessages(Sink s) =>
        s.Messages.Count(m => m.Kind == MessageKinds.Error && (m.Text.Contains("cancelada") || m.Text.Contains("Cancelamento")));

    [Fact]
    public async Task Execucao_que_termina_sem_erro_mas_foi_cancelada_vira_Cancelled_com_uma_mensagem()
    {
        // O MySQL atende o KILL QUERY sem erro (SELECT SLEEP devolve 1): o leitor termina normalmente.
        var (summary, sink) = await RunAsync(r => OneRow(() => r.Cancel("aba")));

        Assert.Equal(RunStatus.Cancelled, summary.Status);
        Assert.Equal(1, CancelMessages(sink));
        // O comando terminou (os efeitos ficam): a mensagem não pode dizer que ele foi cancelado.
        Assert.Contains((MessageKinds.Error, CancelAfterFinish), sink.Messages);
        Assert.DoesNotContain((MessageKinds.Error, GenuineCancel), sink.Messages);
    }

    [Fact]
    public async Task Execucao_normal_sem_cancelamento_continua_Completed()
    {
        var (summary, sink) = await RunAsync(_ => OneRow());

        Assert.Equal(RunStatus.Completed, summary.Status);
        Assert.Equal(0, CancelMessages(sink));
    }

    [Fact]
    public async Task Cancelamento_com_erro_do_driver_vira_Cancelled_sem_mensagem_duplicada()
    {
        var (summary, sink) = await RunAsync(r => { r.Cancel("aba"); throw new DriverFailure("Query execution was interrupted"); });

        Assert.Equal(RunStatus.Cancelled, summary.Status);
        Assert.Equal(1, CancelMessages(sink));
        Assert.Contains((MessageKinds.Error, GenuineCancel), sink.Messages);
        Assert.DoesNotContain((MessageKinds.Error, CancelAfterFinish), sink.Messages);
    }
}
