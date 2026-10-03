using System.Reflection;
using SqlDesk.Core.Connections;
using SqlDesk.Core.Execution;
using SqlDesk.Core.Providers;
using SqlDesk.Core.Sessions;
using SqlDesk.SqlAnalysis;

namespace SqlDesk.Integration.Tests;

/// <summary>
/// Monta o caminho real de execução de uma aba (TabSessionManager + QueryRunner + SessionDb + GuardedRunner) contra
/// um servidor de teste. A conexão é salva num diretório temporário, sem criptografia, e a senha fica só em memória.
/// </summary>
public sealed class Harness : IAsyncDisposable
{
    /// <summary>Sink que guarda as mensagens (para os testes conferirem avisos e erros) e ignora o resto.</summary>
    public sealed class MessageSink : IExecutionSink
    {
        public List<(string Kind, string Text)> Messages { get; } = [];
        public void ResultStarted(int resultIndex, DocRange source, IReadOnlyList<ColumnInfo> columns) { }
        public void Rows(int resultIndex, IReadOnlyList<object?[]> rows) { }
        public void ResultCompleted(int resultIndex, long rowCount, bool truncated) { }
        public void Message(string kind, string text, int? line) => Messages.Add((kind, text));
    }

    /// <summary>Sink que guarda colunas, linhas e mensagens; no modo bruto recebe os tipos originais do .NET (caminho da exportação).</summary>
    public sealed class RowSink(bool raw) : IExecutionSink
    {
        public bool WantsRawValues => raw;
        public List<IReadOnlyList<ColumnInfo>> Columns { get; } = [];
        public List<object?[]> AllRows { get; } = [];
        public List<(string Kind, string Text)> Messages { get; } = [];
        public void ResultStarted(int resultIndex, DocRange source, IReadOnlyList<ColumnInfo> columns) => Columns.Add(columns);
        public void Rows(int resultIndex, IReadOnlyList<object?[]> rows) => AllRows.AddRange(rows);
        public void ResultCompleted(int resultIndex, long rowCount, bool truncated) { }
        public void Message(string kind, string text, int? line) => Messages.Add((kind, text));
    }

    private sealed class NoProtector : IPasswordProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    /// <summary>Analisador que delega tudo, mas nunca acusa commit implícito (usa o padrão da interface).</summary>
    private sealed class BlindAnalyzer(ISqlAnalyzer inner) : ISqlAnalyzer
    {
        public ScriptAnalysis Analyze(string script) => inner.Analyze(script);
        public LocateResult Locate(string text, int cursor) => inner.Locate(text, cursor);
        public RewriteResult Rewrite(string script) => inner.Rewrite(script);
        public bool IsReadOnly(string script, out string? reason) => inner.IsReadOnly(script, out reason);
    }

    /// <summary>Provedor real com o analisador cego no lugar (o resto passa direto).</summary>
    public class BlindProvider : DispatchProxy
    {
        private IDatabaseProvider _inner = null!;

        public static IDatabaseProvider Wrap(IDatabaseProvider inner)
        {
            var proxy = Create<IDatabaseProvider, BlindProvider>();
            ((BlindProvider)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_Analyzer") return new BlindAnalyzer(_inner.Analyzer);
            try { return method.Invoke(_inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
        }
    }

    /// <summary>SessionDb real, mas que entrega à trava o provedor com o analisador cego.</summary>
    private sealed class BlindDb(SessionDb inner) : ISessionDb
    {
        public IDatabaseProvider ProviderOf(string tabId) => BlindProvider.Wrap(inner.ProviderOf(tabId));
        public Task<int> TranCountAsync(string tabId, CancellationToken ct) => inner.TranCountAsync(tabId, ct);
        public Task ExecAsync(string tabId, string sql, CancellationToken ct) => inner.ExecAsync(tabId, sql, ct);
        public Task<long?> CountAsync(string tabId, string sql, CancellationToken ct) => inner.CountAsync(tabId, sql, ct);
        public void SetTrackedTransactions(string tabId, int count) => inner.SetTrackedTransactions(tabId, count);
        public Task<IReadOnlyList<string>?> NonTransactionalTablesAsync(string tabId, IReadOnlyList<string> targets, CancellationToken ct) =>
            inner.NonTransactionalTablesAsync(tabId, targets, ct);
        public Task<bool> RollbackLeftChangesAsync(string tabId, CancellationToken ct) => inner.RollbackLeftChangesAsync(tabId, ct);
    }

    private readonly string _dir;

    private Harness(string dir, TabSessionManager sessions, bool blindToImplicitCommit)
    {
        _dir = dir;
        Sessions = sessions;
        Runner = new QueryRunner(sessions);
        Db = new SessionDb(sessions);
        Guard = new GuardedRunner(blindToImplicitCommit ? new BlindDb(Db) : Db, Runner);
    }

    public string TabId => "t1";
    public TabSessionManager Sessions { get; }
    public QueryRunner Runner { get; }
    public SessionDb Db { get; }
    public GuardedRunner Guard { get; }

    /// <summary>Mensagens da última chamada a <see cref="RunGuardedAsync"/>.</summary>
    public MessageSink LastSink { get; private set; } = new();

    /// <param name="blindToImplicitCommit">
    /// A trava não enxerga os comandos que confirmam sozinhos (só para testar a marca de savepoint, a defesa em tempo de
    /// execução contra commits que a análise léxica não vê, como dentro de uma procedure).
    /// </param>
    public static async Task<Harness> OpenAsync(string server, bool blindToImplicitCommit = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqldesk-it-" + Guid.NewGuid().ToString("N"));
        var store = new ConnectionStore(Path.Combine(dir, "c.json"), new NoProtector());
        var id = store.Save(new SaveConnectionRequest(null, server, "#112233", TestServers.For(server), null)).Id;
        var sessions = new TabSessionManager(store);
        var h = new Harness(dir, sessions, blindToImplicitCommit);
        await sessions.OpenAsync(h.TabId, id, TestServers.Password);
        return h;
    }

    private ExecutionPlan Plan(string sql) =>
        ExecutionPlanner.Plan(Sessions.GetProvider(TabId)!.Analyzer, sql, 0, 0, 0, wholeScript: true);

    /// <summary>
    /// Preparação e limpeza dos testes: roda o script direto pelo QueryRunner, sem as travas (mesmo que a análise
    /// ache perigo, como no DROP TABLE da limpeza). Falha se o script der erro.
    /// </summary>
    public async Task RunAsync(string sql)
    {
        var (batches, offset, line) = Plan(sql) switch
        {
            ExecutionPlan.Runnable r => (r.Batches, r.BaseOffset, r.BaseLine),
            ExecutionPlan.Dangerous d => (d.Batches, d.BaseOffset, d.BaseLine),
            var other => throw new InvalidOperationException($"Plano inesperado na preparação: {other}"),
        };
        var sink = new MessageSink();
        var summary = await Runner.RunAsync(TabId, batches, offset, line, null, sink);
        if (summary.Status != RunStatus.Completed)
            throw new InvalidOperationException(
                $"Preparação falhou ({summary.Status}): {string.Join(" | ", sink.Messages.Select(m => m.Text))}");
    }

    /// <summary>
    /// Executa a consulta pelo QueryRunner real e devolve o que a grade ou a exportação receberiam: com
    /// <paramref name="raw"/> falso, os valores já convertidos pelo CellValues (via CapturingSink); com verdadeiro, os brutos.
    /// </summary>
    public async Task<(RunSummary Summary, RowSink Rows, CapturingSink Capture)> RunCapturingAsync(string sql, bool raw)
    {
        var (batches, offset, line) = Plan(sql) switch
        {
            ExecutionPlan.Runnable r => (r.Batches, r.BaseOffset, r.BaseLine),
            ExecutionPlan.Dangerous d => (d.Batches, d.BaseOffset, d.BaseLine),
            var other => throw new InvalidOperationException($"Plano inesperado: {other}"),
        };
        var rows = new RowSink(raw);
        var capture = new CapturingSink(rows);
        var summary = await Runner.RunAsync(TabId, batches, offset, line, null, raw ? rows : capture);
        return (summary, rows, capture);
    }

    /// <summary>Planeja esperando perigo (primeira confirmação já dada) e executa pelo GuardedRunner.</summary>
    public async Task<GuardOutcome> RunGuardedAsync(string sql)
    {
        var plan = Assert.IsType<ExecutionPlan.Dangerous>(Plan(sql));
        LastSink = new MessageSink();
        return await Guard.RunAsync(TabId, Guid.NewGuid().ToString("N"), plan, null, LastSink, default);
    }

    /// <summary>Executa uma consulta escalar pela conexão da aba (vê o estado da transação da própria aba).</summary>
    public async Task<long> ScalarAsync(string sql)
    {
        await using var cmd = Sessions.GetConnection(TabId)!.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async ValueTask DisposeAsync()
    {
        Guard.Discard(TabId);
        await Sessions.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { /* pasta temporária */ }
    }
}
