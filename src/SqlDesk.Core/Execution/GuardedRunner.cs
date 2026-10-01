using System.Collections.Concurrent;
using System.Diagnostics;
using SqlDesk.SqlAnalysis;

namespace SqlDesk.Core.Execution;

/// <summary>
/// Fluxo de dupla confirmação, a partir da segunda etapa (a primeira, o diálogo de aviso, é do frontend): abre uma
/// transação (ou um savepoint, se a aba já tem uma), executa o texto reescrito com <c>OUTPUT</c> e deixa a decisão
/// (commit ou rollback) pendente. Sem resposta no prazo, faz rollback sozinho para não manter locks.
/// </summary>
public sealed class GuardedRunner(ISessionDb db, IBatchRunner runner, TimeSpan? timeout = null)
{
    /// <summary>Erro do SQL Server: OUTPUT sem INTO não é permitido em tabela com trigger.</summary>
    public const int OutputWithTriggerError = 334;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private sealed record Scope(bool OpenedTransaction, string? Savepoint);

    private sealed class Pending(string guardId, string executionId, Scope scope, CancellationTokenSource timer)
    {
        public string GuardId { get; } = guardId;
        public string ExecutionId { get; } = executionId;
        public Scope Scope { get; } = scope;
        public CancellationTokenSource Timer { get; } = timer;
    }

    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    /// <summary>Chamado quando o prazo acaba e o rollback automático já foi feito.</summary>
    public Action<GuardExpired>? Expired { get; set; }

    public bool HasPending(string tabId) => _pending.ContainsKey(tabId);

    public async Task<GuardOutcome> RunAsync(
        string tabId, string executionId, ExecutionPlan.Dangerous plan, int? maxRows, IExecutionSink sink, CancellationToken ct)
    {
        if (HasPending(tabId))
            throw new GuardPendingException("Há uma decisão de commit/rollback pendente nesta aba. Resolva-a antes de executar outro comando.");
        if (runner.IsRunning(tabId))
            throw new TabBusyException("Esta aba já está executando um comando.");

        var watch = Stopwatch.StartNew();
        var baseCount = await db.TranCountAsync(tabId, ct);
        var scope = await OpenScopeAsync(tabId, baseCount, ct);
        sink.Message(MessageKinds.Info, scope.OpenedTransaction
            ? "Transação aberta para esta execução: nada é gravado até você confirmar."
            : $"A aba já tinha uma transação aberta: usando o savepoint {scope.Savepoint} para poder desfazer só este trecho.", null);

        try
        {
            // TRUNCATE/DROP: conta as linhas antes (dentro da transação, para refletir o estado real).
            var counts = new Dictionary<int, long?>();
            foreach (var d in plan.Dangers.Where(d => d.CountQueries.Count > 0))
                counts[d.Start] = await CountAllAsync(tabId, d.CountQueries, sink, ct);

            var rewrite = OutputRewriter.Rewrite(plan.Text);
            var capture = new CapturingSink(sink);
            var summary = await runner.RunAsync(tabId, rewrite.Batches, plan.BaseOffset, plan.BaseLine, maxRows, capture, ct);

            var previewUnavailable = false;
            if (summary.Status == RunStatus.Error && summary.ErrorNumber == OutputWithTriggerError && rewrite.Rewritten.Count > 0)
            {
                // Tabela com trigger: desfaz e refaz sem OUTPUT. Continua dentro de uma transação e com a segunda confirmação.
                sink.Message(MessageKinds.Info,
                    "Uma das tabelas tem trigger e não aceita a amostra do antes e depois (OUTPUT). Desfazendo e executando de novo, " +
                    "ainda dentro da transação, sem a amostra.", null);
                await RollbackScopeAsync(tabId, scope);
                scope = await OpenScopeAsync(tabId, await db.TranCountAsync(tabId, ct), ct);
                capture = new CapturingSink(sink);
                summary = await runner.RunAsync(tabId, plan.Batches, plan.BaseOffset, plan.BaseLine, maxRows, capture, ct);
                previewUnavailable = true;
            }

            if (summary.Status != RunStatus.Completed)
            {
                await RollbackScopeAsync(tabId, scope);
                sink.Message(MessageKinds.Info, "Rollback feito: nenhuma alteração desta execução foi mantida.", null);
                return new GuardOutcome(summary.Status, watch.ElapsedMilliseconds, summary.TotalRows, null);
            }

            var nowCount = await db.TranCountAsync(tabId, ct);
            var expectedAtLeast = scope.OpenedTransaction ? 1 : baseCount;
            if (nowCount < expectedAtLeast)
            {
                sink.Message(MessageKinds.Error,
                    "O próprio script encerrou a transação (COMMIT ou ROLLBACK). As alterações não podem mais ser desfeitas por aqui.", null);
                return new GuardOutcome(GuardStatus.TransactionLost, watch.ElapsedMilliseconds, summary.TotalRows, null);
            }

            var (changes, approximate) = GuardReportBuilder.Build(plan.Dangers, rewrite, capture.Sets, counts, previewUnavailable, plan.BaseLine);
            var guardId = Guid.NewGuid().ToString("N");
            var info = new GuardInfo(guardId, (int)_timeout.TotalSeconds, !scope.OpenedTransaction, previewUnavailable, approximate,
                capture.TotalAffected, changes);
            Arm(tabId, executionId, guardId, scope);
            return new GuardOutcome(GuardStatus.PendingDecision, watch.ElapsedMilliseconds, summary.TotalRows, info);
        }
        catch
        {
            // Qualquer falha inesperada (conexão, comando fixo): não deixa transação aberta por conta própria.
            await TryRollbackAsync(tabId, scope);
            throw;
        }
    }

    public async Task<GuardResolution> ResolveAsync(string tabId, string guardId, bool commit, CancellationToken ct = default)
    {
        if (!_pending.TryGetValue(tabId, out var p) || p.GuardId != guardId || !_pending.TryRemove(tabId, out p))
            throw new GuardExpiredException("Esta decisão não está mais pendente: o prazo acabou e o rollback automático já foi feito.");
        p.Timer.Cancel();
        p.Timer.Dispose();

        if (commit)
        {
            if (p.Scope.OpenedTransaction) await db.ExecAsync(tabId, "WHILE @@TRANCOUNT > 0 COMMIT TRANSACTION", ct);
            return new GuardResolution(p.ExecutionId, true, p.Scope.OpenedTransaction
                ? "Commit feito: as alterações foram gravadas."
                : "Alterações mantidas na transação que já estava aberta nesta aba (ainda precisam de commit).");
        }

        await RollbackScopeAsync(tabId, p.Scope, ct);
        return new GuardResolution(p.ExecutionId, false, "Rollback feito: as alterações foram desfeitas.");
    }

    /// <summary>Esquece a decisão pendente sem tocar no banco (a conexão está sendo fechada; o servidor desfaz a transação).</summary>
    public void Discard(string tabId)
    {
        if (_pending.TryRemove(tabId, out var p))
        {
            p.Timer.Cancel();
            p.Timer.Dispose();
        }
    }

    private void Arm(string tabId, string executionId, string guardId, Scope scope)
    {
        var timer = new CancellationTokenSource();
        _pending[tabId] = new Pending(guardId, executionId, scope, timer);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(_timeout, timer.Token); }
            catch (OperationCanceledException) { return; }

            // Quem tirar a decisão do dicionário primeiro é quem a executa (resposta do usuário ou este prazo).
            if (!_pending.TryRemove(tabId, out var p) || p.GuardId != guardId) return;
            p.Timer.Dispose();
            string message;
            try
            {
                await RollbackScopeAsync(tabId, p.Scope);
                message = $"Sem resposta em {(int)_timeout.TotalSeconds} segundos: rollback automático feito, as alterações foram desfeitas.";
            }
            catch (Exception ex)
            {
                message = $"Sem resposta em {(int)_timeout.TotalSeconds} segundos, mas o rollback automático falhou: {ex.Message}";
            }
            Expired?.Invoke(new GuardExpired(tabId, p.ExecutionId, message));
        });
    }

    private async Task<Scope> OpenScopeAsync(string tabId, int currentCount, CancellationToken ct)
    {
        if (currentCount == 0)
        {
            await db.ExecAsync(tabId, "BEGIN TRANSACTION", ct);
            return new Scope(true, null);
        }
        var savepoint = "sqldesk_" + Guid.NewGuid().ToString("N")[..8];
        await db.ExecAsync(tabId, $"SAVE TRANSACTION {savepoint}", ct);
        return new Scope(false, savepoint);
    }

    private Task RollbackScopeAsync(string tabId, Scope scope, CancellationToken ct = default) =>
        db.ExecAsync(tabId, scope.OpenedTransaction ? "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION" : $"ROLLBACK TRANSACTION {scope.Savepoint}", ct);

    private async Task TryRollbackAsync(string tabId, Scope scope)
    {
        try { await RollbackScopeAsync(tabId, scope); }
        catch { /* a conexão pode ter caído: nesse caso o servidor já desfez a transação */ }
    }

    private async Task<long?> CountAllAsync(string tabId, IReadOnlyList<string> queries, IExecutionSink sink, CancellationToken ct)
    {
        long total = 0;
        foreach (var q in queries)
        {
            try
            {
                if (await db.CountAsync(tabId, q, ct) is { } n) total += n;
                else return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sink.Message(MessageKinds.Info, $"Não foi possível contar as linhas antes de executar: {ex.Message}", null);
                return null;
            }
        }
        return total;
    }
}
