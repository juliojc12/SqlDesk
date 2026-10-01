using SqlDesk.Core.Execution;
using SqlDesk.SqlAnalysis;

namespace SqlDesk.Core.Tests;

public class GuardedRunnerTests
{
    private sealed class FakeDb : ISessionDb
    {
        public int Count;
        public List<string> Commands { get; } = [];
        public Dictionary<string, long?> Counts { get; } = [];

        public Task<int> TranCountAsync(string tabId, CancellationToken ct) => Task.FromResult(Count);

        public Task ExecAsync(string tabId, string sql, CancellationToken ct)
        {
            Commands.Add(sql);
            if (sql == "BEGIN TRANSACTION") Count++;
            else if (sql.Contains("ROLLBACK TRANSACTION") && !sql.Contains("sqldesk_") || sql.StartsWith("WHILE", StringComparison.Ordinal)) Count = 0;
            return Task.CompletedTask;
        }

        public Task<long?> CountAsync(string tabId, string sql, CancellationToken ct)
        {
            Commands.Add(sql);
            return Task.FromResult(Counts.TryGetValue(sql, out var n) ? n : 5);
        }
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
        var plan = ExecutionPlanner.Plan(sql, 0, 0, 0, wholeScript: true);
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
}
