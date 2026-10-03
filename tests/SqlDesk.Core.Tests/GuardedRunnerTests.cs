using SqlDesk.Core.Execution;
using SqlDesk.Core.Providers;
using SqlDesk.SqlAnalysis;

namespace SqlDesk.Core.Tests;

public class GuardedRunnerTests
{
    private sealed class FakeDb : ISessionDb
    {
        public int Count;
        public List<string> Commands { get; } = [];
        public Dictionary<string, long?> Counts { get; } = [];
        public List<int> Tracked { get; } = [];

        public void SetTrackedTransactions(string tabId, int count) => Tracked.Add(count);

        public IDatabaseProvider Provider = ProviderRegistry.Get(ProviderIds.SqlServer);

        public IDatabaseProvider ProviderOf(string tabId) => Provider;

        public Task<int> TranCountAsync(string tabId, CancellationToken ct) => Task.FromResult(Count);

        /// <summary>Simula erro do servidor num comando (devolve a exceção a lançar, ou null).</summary>
        public Func<string, Exception?>? Fail;

        public Task ExecAsync(string tabId, string sql, CancellationToken ct)
        {
            Commands.Add(sql);
            if (Fail?.Invoke(sql) is { } error) throw error;
            if (sql == "BEGIN TRANSACTION" || sql == "START TRANSACTION") Count++;
            else if (sql.Contains("ROLLBACK TRANSACTION") && !sql.Contains("sqldesk_") || sql.StartsWith("WHILE", StringComparison.Ordinal)) Count = 0;
            else if (sql == "COMMIT" || sql == "ROLLBACK") Count = 0; // MySQL: ROLLBACK TO SAVEPOINT mantém a transação
            return Task.CompletedTask;
        }

        public Task<long?> CountAsync(string tabId, string sql, CancellationToken ct)
        {
            Commands.Add(sql);
            return Task.FromResult(Counts.TryGetValue(sql, out var n) ? n : 5);
        }

        /// <summary>Simula a consulta de mecanismo: recebe os alvos e devolve os não transacionais (null = falhou). Sem valor: todos InnoDB.</summary>
        public Func<IReadOnlyList<string>, IReadOnlyList<string>?>? Engines;
        public List<IReadOnlyList<string>> EngineChecks { get; } = [];
        /// <summary>O servidor avisa (1196) que o ROLLBACK não desfez tabela não transacional.</summary>
        public bool RollbackMissed;

        public Task<IReadOnlyList<string>?> NonTransactionalTablesAsync(string tabId, IReadOnlyList<string> targets, CancellationToken ct)
        {
            EngineChecks.Add(targets);
            return Task.FromResult(Engines is null ? [] : Engines(targets));
        }

        public Task<bool> RollbackLeftChangesAsync(string tabId, CancellationToken ct) => Task.FromResult(RollbackMissed);
    }

    private sealed class NullSink : IExecutionSink
    {
        public List<string> Messages { get; } = [];
        public void ResultStarted(int resultIndex, DocRange source, IReadOnlyList<ColumnInfo> columns) { }
        public void Rows(int resultIndex, IReadOnlyList<object?[]> rows) { }
        public void ResultCompleted(int resultIndex, long rowCount, bool truncated) { }
        public void Message(string kind, string text, int? line) => Messages.Add(text);
    }

    /// <summary>Cada chamada consome o próximo roteiro e o aplica ao sink, como o SqlDataReader faria.</summary>
    private sealed class FakeRunner : IBatchRunner
    {
        public Queue<Func<IExecutionSink, FakeDb, RunSummary>> Script { get; } = new();
        public List<IReadOnlyList<Batch>> Calls { get; } = [];
        public FakeDb Db = null!;
        public bool IsRunning(string tabId) => false;

        public Task<RunSummary> RunAsync(string tabId, IReadOnlyList<Batch> batches, int baseOffset, int baseLine, int? maxRows,
            IExecutionSink sink, CancellationToken externalCt = default)
        {
            Calls.Add(batches);
            return Task.FromResult(Script.Dequeue()(sink, Db));
        }
    }

    private static readonly DocRange Src = new(0, 1);
    private static readonly ColumnInfo[] BeforeAfter =
        [new("Id", "number", "int"), new("Nome", "text", "nvarchar"), new("Id", "number", "int"), new("Nome", "text", "nvarchar")];

    /// <summary>UPDATE com OUTPUT deleted.*, inserted.*: 2 linhas, Nome muda.</summary>
    private static RunSummary UpdateOutput(IExecutionSink s, FakeDb _)
    {
        s.ResultStarted(0, Src, BeforeAfter);
        s.Rows(0, [new object?[] { 1, "a", 1, "X" }, new object?[] { 2, "b", 2, "X" }]);
        s.ResultCompleted(0, 2, false);
        s.StatementCompleted(2);
        return new RunSummary(RunStatus.Completed, 10, 2);
    }

    private static (GuardedRunner Guard, FakeDb Db, FakeRunner Runner) Make(TimeSpan? timeout = null)
    {
        var db = new FakeDb();
        var runner = new FakeRunner { Db = db };
        return (new GuardedRunner(db, runner, timeout), db, runner);
    }

    private static ExecutionPlan.Dangerous Plan(string sql)
    {
        var plan = ExecutionPlanner.Plan(SqlServerAnalyzer.Instance, sql, 0, 0, 0, wholeScript: true);
        return Assert.IsType<ExecutionPlan.Dangerous>(plan);
    }

    [Fact]
    public async Task UPDATE_sem_WHERE_abre_transacao_reescreve_com_OUTPUT_e_deixa_a_decisao_pendente()
    {
        var (guard, db, runner) = Make();
        runner.Script.Enqueue(UpdateOutput);

        var outcome = await guard.RunAsync("t", "e1", Plan("UPDATE dbo.Clientes SET Nome = 'X'"), 10_000, new NullSink(), default);

        Assert.Equal("BEGIN TRANSACTION", db.Commands[0]);
        Assert.Contains("OUTPUT", runner.Calls.Single().Single().Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GuardStatus.PendingDecision, outcome.Status);
        var info = outcome.Pending!;
        Assert.False(info.UsesSavepoint);
        Assert.Equal(120, info.TimeoutSeconds);

        var change = Assert.Single(info.Changes);
        Assert.Equal(DangerKind.UpdateWithoutWhere, change.Kind);
        Assert.Equal("dbo.Clientes", change.Target);
        Assert.Equal(2, change.AffectedRows);
        Assert.True(change.HasPreview);
        Assert.Equal(["Id", "Nome"], change.Columns);
        Assert.Equal(["a", "b"], change.Before.Select(r => r[1]));
        Assert.Equal(["X", "X"], change.After.Select(r => r[1]));
        Assert.True(guard.HasPending("t"));
    }

    [Fact]
    public async Task Commit_grava_e_uma_segunda_resposta_nao_vale()
    {
        var (guard, db, runner) = Make();
        runner.Script.Enqueue(UpdateOutput);
        var info = (await guard.RunAsync("t", "e1", Plan("UPDATE dbo.C SET a = 1"), null, new NullSink(), default)).Pending!;

        var r = await guard.ResolveAsync("t", info.GuardId, commit: true);

        Assert.True(r.Committed);
        Assert.Equal("e1", r.ExecutionId);
        Assert.Equal("WHILE @@TRANCOUNT > 0 COMMIT TRANSACTION", db.Commands.Last());
        Assert.False(guard.HasPending("t"));
        await Assert.ThrowsAsync<GuardExpiredException>(() => guard.ResolveAsync("t", info.GuardId, true));
    }

    [Fact]
    public async Task Rollback_desfaz_tudo()
    {
        var (guard, db, runner) = Make();
        runner.Script.Enqueue(UpdateOutput);
        var info = (await guard.RunAsync("t", "e1", Plan("DELETE FROM dbo.C"), null, new NullSink(), default)).Pending!;

        var r = await guard.ResolveAsync("t", info.GuardId, commit: false);

        Assert.False(r.Committed);
        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", db.Commands.Last());
        Assert.Equal(0, db.Count);
    }

    [Fact]
    public async Task Guarda_so_a_decisao_do_guardId_correto()
    {
        var (guard, _, runner) = Make();
        runner.Script.Enqueue(UpdateOutput);
        await guard.RunAsync("t", "e1", Plan("UPDATE dbo.C SET a = 1"), null, new NullSink(), default);

        await Assert.ThrowsAsync<GuardExpiredException>(() => guard.ResolveAsync("t", "outro-id", true));
        Assert.True(guard.HasPending("t"));
    }

    [Fact]
    public async Task Aba_com_transacao_aberta_usa_savepoint_e_so_desfaz_o_trecho()
    {
        var (guard, db, runner) = Make();
        db.Count = 1;
        runner.Script.Enqueue(UpdateOutput);

        var info = (await guard.RunAsync("t", "e1", Plan("UPDATE dbo.C SET a = 1"), null, new NullSink(), default)).Pending!;

        Assert.True(info.UsesSavepoint);
        Assert.DoesNotContain("BEGIN TRANSACTION", db.Commands);
        var save = db.Commands[0];
        Assert.StartsWith("SAVE TRANSACTION sqldesk_", save);
        var name = save["SAVE TRANSACTION ".Length..];

        await guard.ResolveAsync("t", info.GuardId, commit: false);
        Assert.Equal($"ROLLBACK TRANSACTION {name}", db.Commands.Last());
        Assert.Equal(1, db.Count); // a transação do usuário continua aberta
    }

    [Fact]
    public async Task Commit_com_savepoint_mantem_na_transacao_do_usuario_sem_comando()
    {
        var (guard, db, runner) = Make();
        db.Count = 1;
        runner.Script.Enqueue(UpdateOutput);
        var info = (await guard.RunAsync("t", "e1", Plan("UPDATE dbo.C SET a = 1"), null, new NullSink(), default)).Pending!;
        var before = db.Commands.Count;

        var r = await guard.ResolveAsync("t", info.GuardId, commit: true);

        Assert.True(r.Committed);
        Assert.Equal(before, db.Commands.Count);
        Assert.Contains("ainda precisam de commit", r.Message);
    }

    [Fact]
    public async Task TRUNCATE_conta_as_linhas_antes_e_nao_reescreve()
    {
        var (guard, db, runner) = Make();
        db.Counts["SELECT COUNT_BIG(*) FROM [dbo].[Clientes]"] = 1234;
        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Completed, 5, 0));

        var info = (await guard.RunAsync("t", "e1", Plan("TRUNCATE TABLE dbo.Clientes"), null, new NullSink(), default)).Pending!;

        Assert.Equal(["BEGIN TRANSACTION", "SELECT COUNT_BIG(*) FROM [dbo].[Clientes]"], db.Commands.Take(2));
        var change = Assert.Single(info.Changes);
        Assert.Equal(DangerKind.TruncateTable, change.Kind);
        Assert.Equal(1234, change.AffectedRows);
        Assert.False(change.HasPreview);
        Assert.DoesNotContain("OUTPUT", runner.Calls.Single().Single().Text);
    }

    [Fact]
    public async Task DROP_de_varias_tabelas_soma_as_linhas()
    {
        var (guard, db, runner) = Make();
        db.Counts["SELECT COUNT_BIG(*) FROM [dbo].[A]"] = 10;
        db.Counts["SELECT COUNT_BIG(*) FROM [dbo].[B]"] = 32;
        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Completed, 5, 0));

        var info = (await guard.RunAsync("t", "e1", Plan("DROP TABLE dbo.A, dbo.B"), null, new NullSink(), default)).Pending!;

        Assert.Equal(42, Assert.Single(info.Changes).AffectedRows);
    }

    [Fact]
    public async Task Erro_334_desfaz_e_repete_sem_OUTPUT_ainda_dentro_de_transacao()
    {
        var (guard, db, runner) = Make();
        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Error, 3, 0, GuardedRunner.OutputWithTriggerError));
        runner.Script.Enqueue((s, _) => { s.StatementCompleted(7); return new RunSummary(RunStatus.Completed, 4, 0); });

        var outcome = await guard.RunAsync("t", "e1", Plan("UPDATE dbo.ComTrigger SET a = 1"), null, new NullSink(), default);

        Assert.Equal(["BEGIN TRANSACTION", "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", "BEGIN TRANSACTION"], db.Commands);
        Assert.Contains("OUTPUT", runner.Calls[0].Single().Text);
        Assert.DoesNotContain("OUTPUT", runner.Calls[1].Single().Text);
        var info = outcome.Pending!;
        Assert.True(info.PreviewUnavailable);
        Assert.Equal(7, info.TotalAffected);
        Assert.False(Assert.Single(info.Changes).HasPreview);
    }

    [Fact]
    public async Task Erro_na_execucao_faz_rollback_e_nao_deixa_decisao_pendente()
    {
        var (guard, db, runner) = Make();
        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Error, 3, 0, 547));

        var outcome = await guard.RunAsync("t", "e1", Plan("DELETE FROM dbo.C"), null, new NullSink(), default);

        Assert.Equal(RunStatus.Error, outcome.Status);
        Assert.Null(outcome.Pending);
        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", db.Commands.Last());
        Assert.False(guard.HasPending("t"));
    }

    [Fact]
    public async Task Cancelamento_tambem_faz_rollback()
    {
        var (guard, db, runner) = Make();
        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Cancelled, 3, 0));

        var outcome = await guard.RunAsync("t", "e1", Plan("DELETE FROM dbo.C"), null, new NullSink(), default);

        Assert.Equal(RunStatus.Cancelled, outcome.Status);
        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", db.Commands.Last());
    }

    [Fact]
    public async Task Script_que_encerra_a_transacao_nao_gera_decisao_e_avisa()
    {
        var (guard, _, runner) = Make();
        var sink = new NullSink();
        runner.Script.Enqueue((s, db) => { db.Count = 0; return new RunSummary(RunStatus.Completed, 3, 0); });

        var outcome = await guard.RunAsync("t", "e1", Plan("DELETE FROM dbo.C"), null, sink, default);

        Assert.Equal(GuardStatus.TransactionLost, outcome.Status);
        Assert.Null(outcome.Pending);
        Assert.Contains(sink.Messages, m => m.Contains("encerrou a transação"));
    }

    [Fact]
    public async Task Sem_resposta_no_prazo_faz_rollback_automatico_e_avisa()
    {
        var (guard, db, runner) = Make(TimeSpan.FromMilliseconds(150));
        runner.Script.Enqueue(UpdateOutput);
        var expired = new TaskCompletionSource<GuardExpired>();
        guard.Expired = e => expired.TrySetResult(e);

        var info = (await guard.RunAsync("t", "e1", Plan("UPDATE dbo.C SET a = 1"), null, new NullSink(), default)).Pending!;

        var e = await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("t", "e1"), (e.TabId, e.ExecutionId));
        Assert.Contains("rollback automático", e.Message);
        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", db.Commands.Last());
        Assert.Equal(0, db.Count);
        await Assert.ThrowsAsync<GuardExpiredException>(() => guard.ResolveAsync("t", info.GuardId, true));
    }

    [Fact]
    public async Task Resposta_dentro_do_prazo_cancela_o_rollback_automatico()
    {
        var (guard, db, runner) = Make(TimeSpan.FromMilliseconds(200));
        runner.Script.Enqueue(UpdateOutput);
        var fired = false;
        guard.Expired = _ => fired = true;
        var info = (await guard.RunAsync("t", "e1", Plan("UPDATE dbo.C SET a = 1"), null, new NullSink(), default)).Pending!;

        await guard.ResolveAsync("t", info.GuardId, commit: true);
        await Task.Delay(500);

        Assert.False(fired);
        Assert.Single(db.Commands, c => c.Contains("ROLLBACK") || c.StartsWith("WHILE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Com_decisao_pendente_nao_aceita_outra_execucao_na_aba()
    {
        var (guard, _, runner) = Make();
        runner.Script.Enqueue(UpdateOutput);
        await guard.RunAsync("t", "e1", Plan("UPDATE dbo.C SET a = 1"), null, new NullSink(), default);

        await Assert.ThrowsAsync<GuardPendingException>(
            () => guard.RunAsync("t", "e2", Plan("DELETE FROM dbo.C"), null, new NullSink(), default));
    }

    [Fact]
    public async Task Outra_aba_nao_e_afetada_pela_decisao_pendente()
    {
        var (guard, _, runner) = Make();
        runner.Script.Enqueue(UpdateOutput);
        runner.Script.Enqueue(UpdateOutput);
        await guard.RunAsync("a", "e1", Plan("UPDATE dbo.C SET a = 1"), null, new NullSink(), default);

        var other = await guard.RunAsync("b", "e2", Plan("UPDATE dbo.C SET a = 1"), null, new NullSink(), default);

        Assert.Equal(GuardStatus.PendingDecision, other.Status);
    }

    [Fact]
    public async Task Dois_UPDATEs_com_um_result_set_so_viram_correspondencia_aproximada_sem_amostra()
    {
        var (guard, _, runner) = Make();
        runner.Script.Enqueue(UpdateOutput);

        var info = (await guard.RunAsync("t", "e1", Plan("UPDATE dbo.A SET x = 1;\nUPDATE dbo.B SET x = 1;"), null, new NullSink(), default)).Pending!;

        Assert.True(info.Approximate);
        Assert.Equal(2, info.Changes.Count);
        Assert.All(info.Changes, c => Assert.False(c.HasPreview));
        Assert.Equal([1, 2], info.Changes.Select(c => c.Line));
    }

    [Fact]
    public async Task Dois_UPDATEs_com_dois_result_sets_ligam_cada_amostra_ao_seu_statement()
    {
        var (guard, _, runner) = Make();
        runner.Script.Enqueue((s, _) =>
        {
            s.ResultStarted(0, Src, BeforeAfter);
            s.Rows(0, [new object?[] { 1, "a", 1, "X" }]);
            s.ResultCompleted(0, 1, false);
            s.StatementCompleted(1);
            s.ResultStarted(1, Src, BeforeAfter);
            s.Rows(1, [new object?[] { 1, "a", 1, "X" }, new object?[] { 2, "b", 2, "X" }, new object?[] { 3, "c", 3, "X" }]);
            s.ResultCompleted(1, 3, false);
            s.StatementCompleted(3);
            return new RunSummary(RunStatus.Completed, 5, 4);
        });

        var info = (await guard.RunAsync("t", "e1", Plan("UPDATE dbo.A SET x = 1;\nUPDATE dbo.B SET x = 1;"), null, new NullSink(), default)).Pending!;

        Assert.False(info.Approximate);
        Assert.Equal([1L, 3L], info.Changes.Select(c => c.AffectedRows));
        Assert.Equal("dbo.B", info.Changes[1].Target);
    }

    [Fact]
    public async Task Amostra_e_limitada_a_200_linhas_mas_a_contagem_vem_do_servidor()
    {
        var (guard, _, runner) = Make();
        runner.Script.Enqueue((s, _) =>
        {
            s.ResultStarted(0, Src, BeforeAfter);
            s.Rows(0, Enumerable.Range(0, 500).Select(i => new object?[] { i, "a", i, "b" }).ToList());
            s.ResultCompleted(0, 500, false);
            s.StatementCompleted(123_456); // o OUTPUT foi cortado no cliente; o servidor sabe o total
            return new RunSummary(RunStatus.Completed, 5, 500);
        });

        var change = Assert.Single((await guard.RunAsync("t", "e1", Plan("UPDATE dbo.A SET x = 1"), null, new NullSink(), default)).Pending!.Changes);

        Assert.Equal(200, change.Before.Count);
        Assert.Equal(123_456, change.AffectedRows);
    }

    [Fact]
    public async Task DELETE_mostra_so_o_antes()
    {
        var (guard, _, runner) = Make();
        runner.Script.Enqueue((s, _) =>
        {
            s.ResultStarted(0, Src, [new("Id", "number", "int"), new("Nome", "text", "nvarchar")]);
            s.Rows(0, [new object?[] { 1, "a" }]);
            s.ResultCompleted(0, 1, false);
            s.StatementCompleted(1);
            return new RunSummary(RunStatus.Completed, 5, 1);
        });

        var change = Assert.Single((await guard.RunAsync("t", "e1", Plan("DELETE FROM dbo.A"), null, new NullSink(), default)).Pending!.Changes);

        Assert.Equal(DangerKind.DeleteWithoutWhere, change.Kind);
        Assert.Equal(["Id", "Nome"], change.Columns);
        Assert.Single(change.Before);
        Assert.Empty(change.After);
    }

    [Fact]
    public async Task Descartar_esquece_a_decisao_sem_tocar_no_banco()
    {
        var (guard, db, runner) = Make();
        runner.Script.Enqueue(UpdateOutput);
        await guard.RunAsync("t", "e1", Plan("UPDATE dbo.C SET a = 1"), null, new NullSink(), default);
        var before = db.Commands.Count;

        guard.Discard("t");

        Assert.False(guard.HasPending("t"));
        Assert.Equal(before, db.Commands.Count);
    }

    [Fact]
    public async Task Servico_de_transacao_consulta_confirma_e_desfaz_tudo()
    {
        var db = new FakeDb();
        var svc = new TransactionService(db);

        Assert.Equal(1, await svc.BeginAsync("t"));
        Assert.Equal(1, svc.Known("t"));
        Assert.Equal(1, svc.OpenTabs()["t"]);

        await svc.CommitAsync("t");
        Assert.Equal(0, svc.Known("t"));
        Assert.Empty(svc.OpenTabs());
        Assert.Contains("WHILE @@TRANCOUNT > 0 COMMIT TRANSACTION", db.Commands);

        await svc.BeginAsync("t");
        await svc.RollbackAsync("t");
        Assert.Equal(0, svc.Known("t"));
        Assert.Contains("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", db.Commands);
    }
    [Fact]
    public async Task Servico_de_transacao_atualiza_o_contador_rastreado()
    {
        var db = new FakeDb();
        var svc = new TransactionService(db);

        await svc.BeginAsync("t");
        Assert.Equal([1], db.Tracked);
        await svc.CommitAsync("t");
        Assert.Equal([1, 0], db.Tracked);
        await svc.BeginAsync("t");
        await svc.RollbackAsync("t");
        Assert.Equal([1, 0, 1, 0], db.Tracked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Guarda_atualiza_o_contador_rastreado_ao_abrir_e_ao_decidir(bool commit)
    {
        var (guard, db, runner) = Make();
        runner.Script.Enqueue(UpdateOutput);
        var info = (await guard.RunAsync("t", "e1", Plan("UPDATE dbo.C SET a = 1"), null, new NullSink(), default)).Pending!;
        Assert.Equal([1], db.Tracked);

        await guard.ResolveAsync("t", info.GuardId, commit);
        Assert.Equal([1, 0], db.Tracked);
    }

    [Fact]
    public async Task Guarda_com_savepoint_nao_mexe_no_contador_rastreado()
    {
        var (guard, db, runner) = Make();
        db.Count = 1;
        runner.Script.Enqueue(UpdateOutput);
        var info = (await guard.RunAsync("t", "e1", Plan("UPDATE dbo.C SET a = 1"), null, new NullSink(), default)).Pending!;

        await guard.ResolveAsync("t", info.GuardId, commit: false);
        Assert.Empty(db.Tracked);
    }
    [Fact]
    public async Task Mysql_DDL_executa_sem_transacao_e_sem_segunda_confirmacao()
    {
        var db = new FakeDb { Provider = ProviderRegistry.Get(ProviderIds.MySql) };
        var runner = new FakeRunner { Db = db };
        runner.Script.Enqueue((sink, _) => new RunSummary(RunStatus.Completed, 1, 0));
        var guard = new GuardedRunner(db, runner);
        var plan = (ExecutionPlan.Dangerous)ExecutionPlanner.Plan(MySqlAnalyzer.Instance, "DROP TABLE t", 0, 0, 0, wholeScript: true);

        var outcome = await guard.RunAsync("tab", "e1", plan, null, new NullSink(), default);

        Assert.Equal(GuardStatus.Completed, outcome.Status);
        Assert.Null(outcome.Pending);
        Assert.DoesNotContain("START TRANSACTION", db.Commands);
        Assert.False(guard.HasPending("tab"));
    }

    [Fact]
    public async Task Mysql_UPDATE_sem_where_usa_contagem_previa_e_fica_pendente()
    {
        var db = new FakeDb { Provider = ProviderRegistry.Get(ProviderIds.MySql) };
        db.Counts["SELECT COUNT(*) FROM t"] = 42;
        var runner = new FakeRunner { Db = db };
        runner.Script.Enqueue((sink, d) => { sink.StatementCompleted(42); return new RunSummary(RunStatus.Completed, 1, 0); });
        var guard = new GuardedRunner(db, runner);
        var plan = (ExecutionPlan.Dangerous)ExecutionPlanner.Plan(MySqlAnalyzer.Instance, "UPDATE t SET a = 1", 0, 0, 0, wholeScript: true);

        var outcome = await guard.RunAsync("tab", "e1", plan, null, new NullSink(), default);

        Assert.Equal(GuardStatus.PendingDecision, outcome.Status);
        var change = Assert.Single(outcome.Pending!.Changes);
        Assert.Equal(42, change.AffectedRows);
        Assert.False(change.HasPreview);
        Assert.Contains("START TRANSACTION", db.Commands);
        Assert.False(outcome.Pending.PreviewUnavailable);
        Assert.False(outcome.Pending.Approximate);
    }

    [Fact]
    public async Task Mysql_DML_junto_com_DDL_roda_direto_e_e_irreversivel()
    {
        var db = new FakeDb { Provider = ProviderRegistry.Get(ProviderIds.MySql) };
        var runner = new FakeRunner { Db = db };
        runner.Script.Enqueue((sink, _) => new RunSummary(RunStatus.Completed, 1, 0));
        var guard = new GuardedRunner(db, runner);
        var plan = (ExecutionPlan.Dangerous)ExecutionPlanner.Plan(MySqlAnalyzer.Instance, "DELETE FROM a;\nDROP TABLE b;", 0, 0, 0, wholeScript: true);

        Assert.True(GuardedRunner.IsIrreversible(db.Provider, plan));
        var outcome = await guard.RunAsync("tab", "e1", plan, null, new NullSink(), default);

        Assert.Equal(GuardStatus.Completed, outcome.Status);
        Assert.Null(outcome.Pending);
        Assert.DoesNotContain("START TRANSACTION", db.Commands);
    }

    [Fact]
    public async Task Mysql_DDL_com_transacao_aberta_avisa_que_o_commit_implicito_a_confirma()
    {
        var db = new FakeDb { Provider = ProviderRegistry.Get(ProviderIds.MySql), Count = 1 };
        var runner = new FakeRunner { Db = db };
        runner.Script.Enqueue((sink, _) => new RunSummary(RunStatus.Completed, 1, 0));
        var guard = new GuardedRunner(db, runner);
        var sink = new NullSink();
        var plan = (ExecutionPlan.Dangerous)ExecutionPlanner.Plan(MySqlAnalyzer.Instance, "TRUNCATE TABLE t", 0, 0, 0, wholeScript: true);

        var outcome = await guard.RunAsync("tab", "e1", plan, null, sink, default);

        Assert.Equal(GuardStatus.Completed, outcome.Status);
        Assert.Contains(sink.Messages, m => m.Contains("transação que estava aberta"));
        Assert.DoesNotContain(db.Commands, c => c.StartsWith("SAVEPOINT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mysql_DDL_com_erro_devolve_o_status_do_runner()
    {
        var db = new FakeDb { Provider = ProviderRegistry.Get(ProviderIds.MySql) };
        var runner = new FakeRunner { Db = db };
        runner.Script.Enqueue((sink, _) => new RunSummary(RunStatus.Error, 1, 0, 1051));
        var guard = new GuardedRunner(db, runner);
        var plan = (ExecutionPlan.Dangerous)ExecutionPlanner.Plan(MySqlAnalyzer.Instance, "DROP TABLE t", 0, 0, 0, wholeScript: true);

        var outcome = await guard.RunAsync("tab", "e1", plan, null, new NullSink(), default);

        Assert.Equal(RunStatus.Error, outcome.Status);
        Assert.Null(outcome.Pending);
    }

    [Fact]
    public void SqlServer_nunca_e_irreversivel()
    {
        var plan = Plan("DROP TABLE dbo.A; TRUNCATE TABLE dbo.B; ALTER TABLE dbo.C DROP COLUMN x;");
        Assert.False(GuardedRunner.IsIrreversible(ProviderRegistry.Get(ProviderIds.SqlServer), plan));
    }

    [Fact]
    public void Mysql_so_UPDATE_DELETE_nao_e_irreversivel()
    {
        var plan = (ExecutionPlan.Dangerous)ExecutionPlanner.Plan(MySqlAnalyzer.Instance, "UPDATE a SET x = 1; DELETE FROM b;", 0, 0, 0, wholeScript: true);
        Assert.False(GuardedRunner.IsIrreversible(ProviderRegistry.Get(ProviderIds.MySql), plan));
    }

    [Theory]
    [InlineData(DangerKind.TruncateTable, true)]
    [InlineData(DangerKind.Drop, true)]
    [InlineData(DangerKind.DropColumn, true)]
    [InlineData(DangerKind.AlterTable, true)]
    [InlineData(DangerKind.UpdateWithoutWhere, false)]
    [InlineData(DangerKind.DeleteWithoutWhere, false)]
    [InlineData(DangerKind.Unanalyzable, false)]
    public void DangerKinds_IsDdl(DangerKind kind, bool expected) => Assert.Equal(expected, DangerKinds.IsDdl(kind));

    private static readonly IDatabaseProvider MySql = ProviderRegistry.Get(ProviderIds.MySql);

    private static ExecutionPlan.Dangerous MyPlan(string sql) =>
        Assert.IsType<ExecutionPlan.Dangerous>(ExecutionPlanner.Plan(MySqlAnalyzer.Instance, sql, 0, 0, 0, wholeScript: true));

    /// <summary>Erro do MySQL com número (ex.: 1305, SAVEPOINT does not exist). O construtor é interno no MySqlConnector.</summary>
    private static MySqlConnector.MySqlException MySqlError(int code, string message) =>
        (MySqlConnector.MySqlException)Activator.CreateInstance(typeof(MySqlConnector.MySqlException),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
            [(MySqlConnector.MySqlErrorCode)code, message], null)!;

    [Theory]
    [InlineData("DELETE FROM t; START TRANSACTION;")]
    [InlineData("DELETE FROM t; BEGIN;")]
    [InlineData("DELETE FROM t; CREATE TABLE x (id INT); BEGIN;")]
    [InlineData("DELETE FROM t; COMMIT; START TRANSACTION;")]
    [InlineData("SET autocommit=0; DELETE FROM t; CREATE TABLE x (id INT);")]
    [InlineData("DELETE FROM t; CREATE TABLE x (id INT);")]
    [InlineData("DELETE FROM t; CREATE INDEX ix ON t(id);")]
    [InlineData("DELETE FROM t; RENAME TABLE a TO b;")]
    [InlineData("DELETE FROM t; LOCK TABLES t WRITE; UNLOCK TABLES;")]
    [InlineData("DELETE FROM t; ANALYZE TABLE t;")]
    [InlineData("CREATE TABLE x (id INT); DELETE FROM t;")]
    [InlineData("UPDATE t SET a = 1; CALL p();")]
    [InlineData("BEGIN NOT ATOMIC DELETE FROM t; CREATE TABLE x (id INT); END")]
    [InlineData("DELETE FROM t; SET STATEMENT max_statement_time=100 FOR CREATE TABLE x (id INT);")]
    public void Mysql_DML_perigoso_com_commit_implicito_no_trecho_e_irreversivel(string sql)
    {
        Assert.True(GuardedRunner.IsIrreversible(MySql, MyPlan(sql)));
        // No SQL Server o mesmo texto nunca é irreversível: ali tudo fica na transação.
        if (ExecutionPlanner.Plan(SqlServerAnalyzer.Instance, sql, 0, 0, 0, wholeScript: true) is ExecutionPlan.Dangerous ss)
            Assert.False(GuardedRunner.IsIrreversible(ProviderRegistry.Get(ProviderIds.SqlServer), ss));
    }

    [Theory]
    [InlineData("DELETE FROM t")]
    [InlineData("UPDATE t SET a = 1; DELETE FROM b; INSERT INTO c VALUES (1); SELECT 1;")]
    [InlineData("DELETE FROM t; SAVEPOINT s; ROLLBACK TO SAVEPOINT s;")]
    public void Mysql_DML_sem_commit_implicito_continua_reversivel(string sql) =>
        Assert.False(GuardedRunner.IsIrreversible(MySql, MyPlan(sql)));

    [Fact]
    public void Mysql_trecho_que_nao_pode_ser_analisado_e_irreversivel()
    {
        var danger = new DangerousStatement(DangerKind.Unanalyzable, 0, 8, 1, "?", null, [], CanPreviewWithOutput: false);
        var plan = new ExecutionPlan.Dangerous("SELECT 1", [new Batch(0, "SELECT 1", 0, 1)], 0, 1, null, [danger], [], []);

        Assert.True(GuardedRunner.IsIrreversible(MySql, plan));
        Assert.False(GuardedRunner.IsIrreversible(ProviderRegistry.Get(ProviderIds.SqlServer), plan));
    }

    [Fact]
    public async Task Mysql_marca_a_transacao_com_savepoint_e_confere_no_fim()
    {
        var db = new FakeDb { Provider = MySql };
        var runner = new FakeRunner { Db = db };
        runner.Script.Enqueue((s, _) => { s.StatementCompleted(3); return new RunSummary(RunStatus.Completed, 1, 0); });
        var guard = new GuardedRunner(db, runner);

        var outcome = await guard.RunAsync("t", "e1", MyPlan("DELETE FROM t"), null, new NullSink(), default);

        Assert.Equal(GuardStatus.PendingDecision, outcome.Status);
        Assert.Equal("START TRANSACTION", db.Commands[0]);
        Assert.StartsWith("SAVEPOINT sqldesk_mark_", db.Commands[1]);
        var mark = db.Commands[1]["SAVEPOINT ".Length..];
        Assert.Equal($"RELEASE SAVEPOINT {mark}", db.Commands.Last());
    }

    [Fact]
    public async Task Mysql_marca_sumiu_quer_dizer_transacao_perdida_e_nao_oferece_rollback()
    {
        var db = new FakeDb { Provider = MySql };
        db.Fail = sql => sql.StartsWith("RELEASE SAVEPOINT sqldesk_mark_", StringComparison.Ordinal)
            ? MySqlError(1305, "SAVEPOINT sqldesk_mark_x does not exist") : null;
        var runner = new FakeRunner { Db = db };
        runner.Script.Enqueue((s, _) => { s.StatementCompleted(3); return new RunSummary(RunStatus.Completed, 1, 0); });
        var guard = new GuardedRunner(db, runner);
        var sink = new NullSink();

        var outcome = await guard.RunAsync("t", "e1", MyPlan("DELETE FROM t"), null, sink, default);

        Assert.Equal(GuardStatus.TransactionLost, outcome.Status);
        Assert.Null(outcome.Pending);
        Assert.False(guard.HasPending("t"));
        Assert.Contains(sink.Messages, m => m.Contains("já foram gravadas"));
        Assert.DoesNotContain(db.Commands, c => c.StartsWith("ROLLBACK", StringComparison.Ordinal));
        Assert.Equal(0, db.Tracked.Last());
    }

    [Fact]
    public async Task Mysql_marca_sumiu_com_erro_no_script_tambem_e_transacao_perdida()
    {
        var db = new FakeDb { Provider = MySql };
        db.Fail = sql => sql.StartsWith("RELEASE SAVEPOINT sqldesk_mark_", StringComparison.Ordinal)
            ? MySqlError(1305, "does not exist") : null;
        var runner = new FakeRunner { Db = db };
        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Error, 1, 0, 1064));
        var guard = new GuardedRunner(db, runner);
        var sink = new NullSink();

        var outcome = await guard.RunAsync("t", "e1", MyPlan("DELETE FROM t"), null, sink, default);

        Assert.Equal(GuardStatus.TransactionLost, outcome.Status);
        Assert.DoesNotContain(sink.Messages, m => m.Contains("Rollback feito"));
        Assert.Contains(sink.Messages, m => m.Contains("confira os dados"));
        Assert.DoesNotContain(db.Commands, c => c.StartsWith("ROLLBACK", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mysql_erro_no_script_com_a_marca_viva_faz_rollback_normal()
    {
        var db = new FakeDb { Provider = MySql };
        var runner = new FakeRunner { Db = db };
        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Error, 1, 0, 1064));
        var guard = new GuardedRunner(db, runner);

        var outcome = await guard.RunAsync("t", "e1", MyPlan("DELETE FROM t"), null, new NullSink(), default);

        Assert.Equal(RunStatus.Error, outcome.Status);
        Assert.Equal("ROLLBACK", db.Commands.Last());
        Assert.Equal(0, db.Count);
    }

    [Fact]
    public async Task Mysql_outro_erro_ao_liberar_a_marca_propaga_e_desfaz()
    {
        var db = new FakeDb { Provider = MySql };
        db.Fail = sql => sql.StartsWith("RELEASE SAVEPOINT", StringComparison.Ordinal) ? MySqlError(2013, "Lost connection") : null;
        var runner = new FakeRunner { Db = db };
        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Completed, 1, 0));
        var guard = new GuardedRunner(db, runner);

        await Assert.ThrowsAsync<MySqlConnector.MySqlException>(
            () => guard.RunAsync("t", "e1", MyPlan("DELETE FROM t"), null, new NullSink(), default));
        Assert.Equal("ROLLBACK", db.Commands.Last());
        Assert.False(guard.HasPending("t"));
    }

    [Fact]
    public async Task Mysql_com_transacao_aberta_usa_savepoint_e_desfaz_ate_ele_nao_ate_a_marca()
    {
        var db = new FakeDb { Provider = MySql, Count = 1 };
        var runner = new FakeRunner { Db = db };
        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Completed, 1, 0));
        var guard = new GuardedRunner(db, runner);

        var info = (await guard.RunAsync("t", "e1", MyPlan("DELETE FROM t"), null, new NullSink(), default)).Pending!;

        Assert.True(info.UsesSavepoint);
        Assert.StartsWith("SAVEPOINT sqldesk_", db.Commands[0]);
        Assert.DoesNotContain("mark", db.Commands[0]);
        Assert.StartsWith("SAVEPOINT sqldesk_mark_", db.Commands[1]);
        await guard.ResolveAsync("t", info.GuardId, commit: false);
        Assert.Equal($"ROLLBACK TO SAVEPOINT {db.Commands[0]["SAVEPOINT ".Length..]}", db.Commands.Last());
    }

    [Fact]
    public async Task SqlServer_nao_usa_marca()
    {
        var (guard, db, runner) = Make();
        runner.Script.Enqueue(UpdateOutput);

        await guard.RunAsync("t", "e1", Plan("UPDATE dbo.C SET a = 1"), null, new NullSink(), default);

        Assert.DoesNotContain(db.Commands, c => c.Contains("mark") || c.StartsWith("RELEASE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mysql_execucao_direta_com_transacao_aberta_zera_o_contador_rastreado()
    {
        var db = new FakeDb { Provider = MySql, Count = 1 };
        var runner = new FakeRunner { Db = db };
        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Completed, 1, 0));
        var guard = new GuardedRunner(db, runner);

        await guard.RunAsync("t", "e1", MyPlan("DROP TABLE t"), null, new NullSink(), default);

        Assert.Equal([0], db.Tracked);
    }

    // ---- Revisão final: tabela não transacional (MyISAM, Aria, MEMORY, view) não tem como ser desfeita ----

    private static (GuardedRunner Guard, FakeDb Db, FakeRunner Runner) MakeMySql(Func<IReadOnlyList<string>, IReadOnlyList<string>?>? engines = null)
    {
        var db = new FakeDb { Provider = MySql, Engines = engines };
        var runner = new FakeRunner { Db = db };
        return (new GuardedRunner(db, runner), db, runner);
    }

    [Fact]
    public async Task Mysql_alvo_InnoDB_continua_reversivel_e_consulta_o_mecanismo()
    {
        var (guard, db, runner) = MakeMySql();
        Assert.False(await guard.IsIrreversibleAsync("t", MyPlan("DELETE FROM `loja`.`pedidos`"), default));
        Assert.Equal(["`loja`.`pedidos`"], db.EngineChecks.Single());

        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Completed, 1, 0));
        var outcome = await guard.RunAsync("t", "e1", MyPlan("DELETE FROM `loja`.`pedidos`"), null, new NullSink(), default);
        Assert.Equal(GuardStatus.PendingDecision, outcome.Status);
        Assert.Contains("START TRANSACTION", db.Commands);
    }

    [Fact]
    public async Task Mysql_alvo_MyISAM_e_irreversivel_e_roda_direto_sem_transacao()
    {
        var (guard, db, runner) = MakeMySql(targets => targets);
        var plan = MyPlan("UPDATE t SET a = 1");
        Assert.True(await guard.IsIrreversibleAsync("t", plan, default));

        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Completed, 1, 0));
        var sink = new NullSink();
        var outcome = await guard.RunAsync("t", "e1", plan, null, sink, default);

        Assert.Equal(GuardStatus.Completed, outcome.Status);
        Assert.Null(outcome.Pending);
        Assert.False(guard.HasPending("t"));
        Assert.DoesNotContain("START TRANSACTION", db.Commands);
        Assert.Contains(sink.Messages, m => m.Contains("não pode ser desfeito") && m.Contains("t") && m.Contains("MyISAM"));
    }

    [Fact]
    public async Task Mysql_falha_ao_conferir_o_mecanismo_e_irreversivel()
    {
        var (nulo, _, _) = MakeMySql(_ => null);
        Assert.True(await nulo.IsIrreversibleAsync("t", MyPlan("DELETE FROM t"), default));

        var (lanca, _, _) = MakeMySql(_ => throw MySqlError(1142, "SELECT command denied"));
        Assert.True(await lanca.IsIrreversibleAsync("t", MyPlan("DELETE FROM t"), default));
    }

    [Fact]
    public async Task Mysql_alvo_nao_reconhecido_e_irreversivel_sem_consultar()
    {
        var (guard, db, _) = MakeMySql();
        Assert.True(await guard.IsIrreversibleAsync("t", MyPlan("DELETE FROM \"t\""), default));
        Assert.Empty(db.EngineChecks);
    }

    [Fact]
    public async Task Mysql_DDL_ja_e_irreversivel_sem_consultar_o_mecanismo()
    {
        var (guard, db, _) = MakeMySql();
        Assert.True(await guard.IsIrreversibleAsync("t", MyPlan("DROP TABLE t"), default));
        Assert.Empty(db.EngineChecks);
    }

    [Fact]
    public async Task SqlServer_nunca_e_irreversivel_e_nunca_consulta_o_mecanismo()
    {
        var (guard, db, runner) = Make();
        db.Engines = targets => targets; // mesmo que respondesse "não transacional", não pergunta
        Assert.False(await guard.IsIrreversibleAsync("t", Plan("DROP TABLE dbo.A; DELETE FROM dbo.B;"), default));

        runner.Script.Enqueue(UpdateOutput);
        var outcome = await guard.RunAsync("t", "e1", Plan("UPDATE dbo.C SET a = 1"), null, new NullSink(), default);
        Assert.Equal(GuardStatus.PendingDecision, outcome.Status);
        Assert.Empty(db.EngineChecks);
    }

    [Fact]
    public async Task Mysql_rollback_com_aviso_1196_diz_que_a_tabela_nao_transacional_nao_foi_desfeita()
    {
        var (guard, db, runner) = MakeMySql();
        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Completed, 1, 0));
        var info = (await guard.RunAsync("t", "e1", MyPlan("DELETE FROM t"), null, new NullSink(), default)).Pending!;
        db.RollbackMissed = true;

        var r = await guard.ResolveAsync("t", info.GuardId, commit: false);

        Assert.False(r.Committed);
        Assert.Contains("não transacionais", r.Message);
        Assert.DoesNotContain("as alterações foram desfeitas", r.Message);
    }

    [Fact]
    public async Task Mysql_rollback_sem_aviso_continua_dizendo_que_desfez()
    {
        var (guard, _, runner) = MakeMySql();
        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Completed, 1, 0));
        var info = (await guard.RunAsync("t", "e1", MyPlan("DELETE FROM t"), null, new NullSink(), default)).Pending!;

        var r = await guard.ResolveAsync("t", info.GuardId, commit: false);

        Assert.Equal("Rollback feito: as alterações foram desfeitas.", r.Message);
    }

    [Fact]
    public async Task Mysql_erro_com_rollback_que_nao_desfaz_tudo_nao_diz_que_nada_ficou()
    {
        var (guard, db, runner) = MakeMySql();
        db.RollbackMissed = true;
        runner.Script.Enqueue((s, _) => new RunSummary(RunStatus.Error, 1, 0, 1064));
        var sink = new NullSink();

        await guard.RunAsync("t", "e1", MyPlan("DELETE FROM t"), null, sink, default);

        Assert.DoesNotContain(sink.Messages, m => m.Contains("nenhuma alteração desta execução foi mantida"));
        Assert.Contains(sink.Messages, m => m.Contains("não transacionais"));
    }
}
