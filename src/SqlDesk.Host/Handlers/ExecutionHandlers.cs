using System.Data;
using SqlDesk.Core.Execution;
using SqlDesk.Core.Sessions;
using SqlDesk.Host.Bridge;

namespace SqlDesk.Host.Handlers;

/// <summary>Traduz os eventos de uma execução em mensagens da ponte (id nulo), na ordem em que acontecem.</summary>
internal sealed class BridgeSink(EventHub hub, string tabId, string executionId) : IExecutionSink
{
    public void ResultStarted(int resultIndex, DocRange source, IReadOnlyList<ColumnInfo> columns) =>
        hub.Publish("query.resultStarted", new QueryResultStartedEvent(tabId, executionId, resultIndex, source, columns));

    public void Rows(int resultIndex, IReadOnlyList<object?[]> rows) =>
        hub.Publish("query.rows", new QueryRowsEvent(tabId, executionId, resultIndex, rows));

    public void ResultCompleted(int resultIndex, long rowCount, bool truncated) =>
        hub.Publish("query.resultCompleted", new QueryResultCompletedEvent(tabId, executionId, resultIndex, rowCount, truncated));

    public void Message(string kind, string text, int? line) =>
        hub.Publish("query.message", new QueryMessageEvent(tabId, executionId, kind, text, line));
}

/// <summary>
/// Consulta <c>@@TRANCOUNT</c> da aba e publica o estado para o frontend. Se a consulta falha porque a conexão caiu,
/// avisa que o servidor desfez a transação (se havia uma).
/// </summary>
public sealed class TransactionNotifier(TransactionService tran, TabSessionManager sessions, EventHub hub)
{
    public async Task<int> RefreshAsync(string tabId)
    {
        var hadTransaction = tran.Known(tabId) > 0;
        try
        {
            var n = await tran.RefreshAsync(tabId);
            hub.Publish("tab.transaction", new TabTransactionEvent(tabId, n));
            return n;
        }
        catch (Exception)
        {
            if (sessions.GetConnection(tabId) is { State: ConnectionState.Open }) return tran.Known(tabId);
            tran.Forget(tabId);
            hub.Publish("tab.transaction", new TabTransactionEvent(tabId, 0));
            hub.Publish("tab.connectionLost", new TabConnectionLostEvent(tabId, hadTransaction));
            return 0;
        }
    }

    public void Publish(string tabId, int count) => hub.Publish("tab.transaction", new TabTransactionEvent(tabId, count));
}

/// <summary>
/// Executa texto do editor. O backend decide o que rodar e passa tudo pela análise de segurança antes do banco;
/// o frontend só envia texto, cursor, seleção e as respostas aos diálogos.
/// </summary>
public sealed class ExecuteHandler(
    QueryRunner runner, GuardedRunner guard, TransactionNotifier notifier, TabSessionManager sessions, EventHub hub)
    : MessageHandler<ExecuteRequest, ExecuteResponse>
{
    public override string Type => "query.execute";

    protected override async Task<ExecuteResponse> HandleAsync(ExecuteRequest r, CancellationToken ct)
    {
        if (runner.IsRunning(r.TabId))
            throw new BridgeException("busy", "Esta aba já está executando um comando.");
        if (guard.HasPending(r.TabId))
            throw new BridgeException("guard_pending", "Há uma decisão de commit/rollback pendente nesta aba. Resolva-a antes de executar outro comando.");

        var sink = new BridgeSink(hub, r.TabId, r.ExecutionId);
        // A análise de segurança depende do dialeto do banco da aba.
        var provider = sessions.GetProvider(r.TabId)
            ?? throw new BridgeException("not_connected", "A aba não está conectada. Conecte antes de executar.");
        var plan = ExecutionPlanner.Plan(provider.Analyzer, r.Text, r.Cursor, r.SelectionStart, r.SelectionEnd, wholeScript: r.Mode == "script");

        try
        {
            switch (plan)
            {
                case ExecutionPlan.Nothing n:
                    return new ExecuteResponse("nothing", 0, 0, n.Message, null);

                case ExecutionPlan.Refused refused:
                    hub.Publish("query.started", new QueryStartedEvent(r.TabId, r.ExecutionId, null));
                    sink.Message(MessageKinds.Error, refused.Message, null);
                    foreach (var b in refused.Blocked) sink.Message(MessageKinds.Error, $"Linha {b.Line}: {b.Description}", b.Line);
                    return new ExecuteResponse("refused", 0, 0, refused.Message, refused.Blocked.Select(b => new BlockedStatement(b.Line, b.Description)).ToList());

                case ExecutionPlan.Dangerous danger when !r.ConfirmDangerous:
                    // Primeira confirmação: nada é executado, nem os statements anteriores do script. Avisa quando a
                    // execução não poderá ser desfeita (mesma decisão que o GuardedRunner usa para rodar direto, inclusive
                    // o mecanismo das tabelas no MySQL/MariaDB: MyISAM não volta com ROLLBACK).
                    return new ExecuteResponse("needs_confirmation", 0, 0, null,
                        danger.Blocked.Select(b => new BlockedStatement(b.Line, b.Description)).ToList(), danger.Range,
                        Irreversible: await guard.IsIrreversibleAsync(r.TabId, danger, ct));

                case ExecutionPlan.Dangerous danger:
                {
                    hub.Publish("query.started", new QueryStartedEvent(r.TabId, r.ExecutionId, danger.Highlight));
                    foreach (var w in danger.Warnings) sink.Message(MessageKinds.Info, $"Aviso: {w}", null);
                    var outcome = await guard.RunAsync(r.TabId, r.ExecutionId, danger, RowLimits.Resolve(r.MaxRows, r.NoRowLimit), sink, ct);
                    await notifier.RefreshAsync(r.TabId);
                    return new ExecuteResponse(outcome.Status, outcome.ElapsedMs, outcome.TotalRows, null, null, danger.Range, outcome.Pending);
                }

                case ExecutionPlan.Runnable run:
                {
                    // Recomendação de TRANSACTION: escrita com WHERE numa aba sem transação aberta. Não bloqueia; só pergunta.
                    if (run.HasWrites && !r.SkipTranAdvice && await notifier.RefreshAsync(r.TabId) == 0)
                        return new ExecuteResponse("advise_transaction", 0, 0, null, null, run.Range);

                    hub.Publish("query.started", new QueryStartedEvent(r.TabId, r.ExecutionId, run.Highlight));
                    foreach (var w in run.Warnings) sink.Message(MessageKinds.Info, $"Aviso: {w}", null);
                    var summary = await runner.RunAsync(
                        r.TabId, run.Batches, run.BaseOffset, run.BaseLine,
                        RowLimits.Resolve(r.MaxRows, r.NoRowLimit), sink, ct);
                    await notifier.RefreshAsync(r.TabId);
                    return new ExecuteResponse(summary.Status, summary.ElapsedMs, summary.TotalRows, null, null);
                }

                default:
                    throw new InvalidOperationException("Plano de execução desconhecido.");
            }
        }
        catch (TabNotConnectedException ex) { throw new BridgeException("not_connected", ex.Message); }
        catch (TabBusyException ex) { throw new BridgeException("busy", ex.Message); }
        catch (GuardPendingException ex) { throw new BridgeException("guard_pending", ex.Message); }
    }
}

public sealed class CancelHandler(QueryRunner runner) : MessageHandler<TabIdRequest, EmptyResponse>
{
    public override string Type => "query.cancel";

    protected override Task<EmptyResponse> HandleAsync(TabIdRequest r, CancellationToken ct)
    {
        runner.Cancel(r.TabId);
        return Task.FromResult(new EmptyResponse());
    }
}
