using SqlDesk.Core.Execution;
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
/// Executa texto do editor. O backend decide o que rodar e passa tudo pela análise de segurança antes do banco;
/// o frontend só envia texto, cursor e seleção.
/// </summary>
public sealed class ExecuteHandler(QueryRunner runner, EventHub hub) : MessageHandler<ExecuteRequest, ExecuteResponse>
{
    public override string Type => "query.execute";

    protected override async Task<ExecuteResponse> HandleAsync(ExecuteRequest r, CancellationToken ct)
    {
        if (runner.IsRunning(r.TabId))
            throw new BridgeException("busy", "Esta aba já está executando um comando.");

        var sink = new BridgeSink(hub, r.TabId, r.ExecutionId);
        var plan = ExecutionPlanner.Plan(r.Text, r.Cursor, r.SelectionStart, r.SelectionEnd, wholeScript: r.Mode == "script");

        switch (plan)
        {
            case ExecutionPlan.Nothing n:
                return new ExecuteResponse("nothing", 0, 0, n.Message, null);

            case ExecutionPlan.Refused refused:
                hub.Publish("query.started", new QueryStartedEvent(r.TabId, r.ExecutionId, null));
                sink.Message(MessageKinds.Error, refused.Message, null);
                foreach (var b in refused.Blocked) sink.Message(MessageKinds.Error, $"Linha {b.Line}: {b.Description}", b.Line);
                return new ExecuteResponse("refused", 0, 0, refused.Message, refused.Blocked.Select(b => new BlockedStatement(b.Line, b.Description)).ToList());

            case ExecutionPlan.Runnable run:
                hub.Publish("query.started", new QueryStartedEvent(r.TabId, r.ExecutionId, run.Highlight));
                foreach (var w in run.Warnings) sink.Message(MessageKinds.Info, $"Aviso: {w}", null);
                try
                {
                    var summary = await runner.RunAsync(
                        r.TabId, run.Batches, run.BaseOffset, run.BaseLine,
                        r.NoRowLimit ? null : ResultStreamer.DefaultMaxRows, sink, ct);
                    return new ExecuteResponse(summary.Status, summary.ElapsedMs, summary.TotalRows, null, null);
                }
                catch (TabNotConnectedException ex) { throw new BridgeException("not_connected", ex.Message); }
                catch (TabBusyException ex) { throw new BridgeException("busy", ex.Message); }

            default:
                throw new InvalidOperationException("Plano de execução desconhecido.");
        }
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
