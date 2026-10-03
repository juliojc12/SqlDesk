using System.Collections.Concurrent;
using System.Data.Common;
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

    /// <summary>
    /// O banco confirma DDL sozinho (MySQL/MariaDB) e o trecho tem DDL perigoso, algum comando que confirma a transação
    /// sozinho (CREATE, START TRANSACTION, COMMIT, LOCK...), algo opaco (CALL, bloco de código) ou não pôde ser analisado:
    /// nada do trecho pode ser desfeito, nem os UPDATE/DELETE que vierem junto. Usado na primeira confirmação (aviso) e
    /// na execução (direto, sem transação). No SQL Server é sempre false.
    /// </summary>
    public static bool IsIrreversible(SqlDesk.Core.Providers.IDatabaseProvider provider, ExecutionPlan.Dangerous plan) =>
        provider.DdlCommitsImplicitly &&
        (plan.Dangers.Any(d => DangerKinds.IsDdl(d.Kind) || d.Kind == DangerKind.Unanalyzable) ||
         plan.Batches.Any(b => provider.Analyzer.CausesImplicitCommit(b.Text)));

    private const string DdlReason =
        "Este trecho não pode ser desfeito: o MySQL/MariaDB confirma sozinho mudanças de estrutura e comandos de " +
        "transação (ou o trecho não pôde ser analisado).";

    /// <summary>
    /// Decisão completa de "não dá para desfazer", usada na primeira confirmação (ExecuteHandler) e na execução (aqui),
    /// para as duas nunca discordarem: as regras léxicas de <see cref="IsIrreversible"/> e, nos bancos que confirmam DDL
    /// sozinhos, o mecanismo de armazenamento dos alvos dos UPDATE/DELETE perigosos. Alvo em tabela não transacional
    /// (MyISAM, Aria, MEMORY...) ou view, alvo não reconhecido e falha na consulta: irreversível (na dúvida, o pior).
    /// No SQL Server devolve null sem consultar nada. null = reversível; senão, o motivo (frase para o usuário).
    /// </summary>
    public async Task<string?> IrreversibleReasonAsync(string tabId, ExecutionPlan.Dangerous plan, CancellationToken ct)
    {
        var provider = db.ProviderOf(tabId);
        if (IsIrreversible(provider, plan)) return DdlReason;
        if (!provider.DdlCommitsImplicitly) return null;

        var targets = plan.Dangers.Where(d => !DangerKinds.IsDdl(d.Kind) && d.Kind != DangerKind.Unanalyzable).Select(d => d.Target).ToList();
        if (targets.Count == 0) return null;
        if (targets.Any(t => t is null))
            return "Este trecho não pode ser desfeito com segurança: não deu para reconhecer a tabela de um dos comandos e conferir " +
                   "se ela guarda as alterações numa transação.";
        IReadOnlyList<string>? nonTransactional;
        try
        {
            nonTransactional = await db.NonTransactionalTablesAsync(tabId, targets.Select(t => t!).Distinct(StringComparer.Ordinal).ToList(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { nonTransactional = null; }
        if (nonTransactional is null)
            return "Este trecho não pode ser desfeito com segurança: não foi possível conferir se as tabelas dos comandos guardam " +
                   "as alterações numa transação (tabela não encontrada ou sem permissão para consultar).";
        return nonTransactional.Count == 0 ? null
            : $"Este trecho não pode ser desfeito: {string.Join(", ", nonTransactional)} não guarda as alterações numa transação " +
              "(mecanismo como MyISAM, Aria ou MEMORY, ou é uma view), e o ROLLBACK não as desfaria.";
    }

    public async Task<bool> IsIrreversibleAsync(string tabId, ExecutionPlan.Dangerous plan, CancellationToken ct) =>
        await IrreversibleReasonAsync(tabId, plan, ct) is not null;

    private const string RollbackMissedMessage =
        "Rollback feito, mas tabelas não transacionais (por exemplo MyISAM) não foram desfeitas: o que foi gravado nelas " +
        "ficou. Confira os dados.";

    public async Task<GuardOutcome> RunAsync(
        string tabId, string executionId, ExecutionPlan.Dangerous plan, int? maxRows, IExecutionSink sink, CancellationToken ct)
    {
        if (HasPending(tabId))
            throw new GuardPendingException("Há uma decisão de commit/rollback pendente nesta aba. Resolva-a antes de executar outro comando.");
        if (runner.IsRunning(tabId))
            throw new TabBusyException("Esta aba já está executando um comando.");

        var watch = Stopwatch.StartNew();
        var baseCount = await db.TranCountAsync(tabId, ct);

        if (await IrreversibleReasonAsync(tabId, plan, ct) is { } why)
        {
            // O MySQL/MariaDB confirma sozinho DDL e comandos de transação, e tabela não transacional não volta com
            // ROLLBACK: não há o que desfazer (nem os UPDATE/DELETE do mesmo trecho), então roda direto, sem transação e
            // sem segunda confirmação.
            sink.Message(MessageKinds.Info, why + " Executando direto, sem transação e sem segunda confirmação.", null);
            if (baseCount > 0)
            {
                sink.Message(MessageKinds.Info,
                    "A aba tinha uma transação aberta: o commit implícito também confirma a transação que estava aberta.", null);
                db.SetTrackedTransactions(tabId, 0);
            }
            var direct = await runner.RunAsync(tabId, plan.Batches, plan.BaseOffset, plan.BaseLine, maxRows, sink, ct);
            return new GuardOutcome(direct.Status == RunStatus.Completed ? GuardStatus.Completed : direct.Status,
                direct.ElapsedMs, direct.TotalRows, null);
        }

        var scope = await OpenScopeAsync(tabId, baseCount, ct);
        string? mark = null;
        sink.Message(MessageKinds.Info, scope.OpenedTransaction
            ? "Transação aberta para esta execução: nada é gravado até você confirmar."
            : $"A aba já tinha uma transação aberta: usando o savepoint {scope.Savepoint} para poder desfazer só este trecho.", null);

        try
        {
            mark = await SetMarkAsync(tabId, ct);

            // TRUNCATE/DROP: conta as linhas antes (dentro da transação, para refletir o estado real).
            var counts = new Dictionary<int, long?>();
            foreach (var d in plan.Dangers.Where(d => d.CountQueries.Count > 0))
                counts[d.Start] = await CountAllAsync(tabId, d.CountQueries, sink, ct);

            var rewrite = db.ProviderOf(tabId).Analyzer.Rewrite(plan.Text);
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
                mark = await SetMarkAsync(tabId, ct);
                capture = new CapturingSink(sink);
                summary = await runner.RunAsync(tabId, plan.Batches, plan.BaseOffset, plan.BaseLine, maxRows, capture, ct);
                previewUnavailable = true;
            }

            // A marca sumiu: algo no script confirmou a transação da trava (e talvez abriu outra). Não há o que desfazer.
            if (!await MarkAliveAsync(tabId, mark, ct))
                return Lost(tabId, sink, watch, summary.TotalRows, afterError: summary.Status != RunStatus.Completed);

            if (summary.Status != RunStatus.Completed)
            {
                var missed = await RollbackScopeAsync(tabId, scope);
                sink.Message(MessageKinds.Info, missed ? RollbackMissedMessage : "Rollback feito: nenhuma alteração desta execução foi mantida.", null);
                return new GuardOutcome(summary.Status, watch.ElapsedMilliseconds, summary.TotalRows, null);
            }

            var nowCount = await db.TranCountAsync(tabId, ct);
            var expectedAtLeast = scope.OpenedTransaction ? 1 : baseCount;
            if (nowCount < expectedAtLeast)
                return Lost(tabId, sink, watch, summary.TotalRows, afterError: false);

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

    private GuardOutcome Lost(string tabId, IExecutionSink sink, Stopwatch watch, long totalRows, bool afterError)
    {
        var mysql = db.ProviderOf(tabId).DdlCommitsImplicitly;
        sink.Message(MessageKinds.Error, mysql && afterError
            // Com erro, o próprio servidor pode ter desfeito a transação inteira (deadlock): não dá para afirmar nada.
            ? "A transação aberta para esta execução foi encerrada no meio do script, por um comando dele ou pelo próprio " +
              "servidor depois do erro (ex.: deadlock). Não é possível saber por aqui o que ficou gravado: confira os dados."
            : mysql
            ? "A transação aberta para esta execução foi encerrada no meio do script (COMMIT, START TRANSACTION/BEGIN ou um " +
              "comando que o MySQL/MariaDB confirma sozinho). As alterações feitas até ali já foram gravadas (a menos que o " +
              "próprio script tenha feito ROLLBACK) e não podem ser desfeitas por aqui."
            : "O próprio script encerrou a transação (COMMIT ou ROLLBACK). As alterações não podem mais ser desfeitas por aqui.", null);
        db.SetTrackedTransactions(tabId, 0);
        return new GuardOutcome(GuardStatus.TransactionLost, watch.ElapsedMilliseconds, totalRows, null);
    }

    /// <summary>
    /// Nos bancos que confirmam DDL sozinhos, marca a transação da trava com um savepoint. Savepoint nunca sobrevive ao
    /// fim da transação, então se ele sumir no fim, a transação acabou no meio, mesmo que o script tenha aberto outra.
    /// </summary>
    private async Task<string?> SetMarkAsync(string tabId, CancellationToken ct)
    {
        var provider = db.ProviderOf(tabId);
        var name = "sqldesk_mark_" + Guid.NewGuid().ToString("N")[..8];
        if (!provider.DdlCommitsImplicitly || provider.Transactions.ReleaseSavepointSql(name) is null) return null;
        await db.ExecAsync(tabId, provider.Transactions.SavepointSql(name), ct);
        return name;
    }

    /// <summary>Libera a marca (o savepoint da trava, criado antes dela, continua). false = a marca não existe mais.</summary>
    private async Task<bool> MarkAliveAsync(string tabId, string? mark, CancellationToken ct)
    {
        if (mark is null) return true;
        var provider = db.ProviderOf(tabId);
        try
        {
            await db.ExecAsync(tabId, provider.Transactions.ReleaseSavepointSql(mark)!, ct);
            return true;
        }
        catch (DbException ex) when (provider.Transactions.SavepointMissingError is { } missing && provider.ErrorNumber(ex) == missing)
        {
            return false;
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
            if (p.Scope.OpenedTransaction)
            {
                await db.ExecAsync(tabId, db.ProviderOf(tabId).Transactions.CommitAllSql, ct);
                db.SetTrackedTransactions(tabId, 0);
            }
            return new GuardResolution(p.ExecutionId, true, p.Scope.OpenedTransaction
                ? "Commit feito: as alterações foram gravadas."
                : "Alterações mantidas na transação que já estava aberta nesta aba (ainda precisam de commit).");
        }

        var left = await RollbackScopeAsync(tabId, p.Scope, ct);
        return new GuardResolution(p.ExecutionId, false, left ? RollbackMissedMessage : "Rollback feito: as alterações foram desfeitas.");
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
                message = await RollbackScopeAsync(tabId, p.Scope)
                    ? $"Sem resposta em {(int)_timeout.TotalSeconds} segundos: rollback automático feito, mas tabelas não transacionais " +
                      "(por exemplo MyISAM) não foram desfeitas: o que foi gravado nelas ficou. Confira os dados."
                    : $"Sem resposta em {(int)_timeout.TotalSeconds} segundos: rollback automático feito, as alterações foram desfeitas.";
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
            await db.ExecAsync(tabId, db.ProviderOf(tabId).Transactions.BeginSql, ct);
            db.SetTrackedTransactions(tabId, 1);
            return new Scope(true, null);
        }
        var savepoint = "sqldesk_" + Guid.NewGuid().ToString("N")[..8];
        await db.ExecAsync(tabId, db.ProviderOf(tabId).Transactions.SavepointSql(savepoint), ct);
        return new Scope(false, savepoint);
    }

    /// <summary>Desfaz a trava. true = o servidor avisou que alterações em tabela não transacional ficaram (MySQL: 1196).</summary>
    private async Task<bool> RollbackScopeAsync(string tabId, Scope scope, CancellationToken ct = default)
    {
        var tx = db.ProviderOf(tabId).Transactions;
        await db.ExecAsync(tabId, scope.OpenedTransaction ? tx.RollbackAllSql : tx.RollbackToSavepointSql(scope.Savepoint!), ct);
        if (scope.OpenedTransaction) db.SetTrackedTransactions(tabId, 0);
        // Rede de segurança para o que a checagem de mecanismo não vê (trigger, UPDATE com JOIN, comando sem perigo no
        // mesmo trecho): nunca dizer "desfeito" quando o servidor avisou que não desfez.
        try { return await db.RollbackLeftChangesAsync(tabId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

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
