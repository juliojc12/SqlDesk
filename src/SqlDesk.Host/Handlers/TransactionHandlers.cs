using SqlDesk.Core.Execution;
using SqlDesk.Host.Bridge;

namespace SqlDesk.Host.Handlers;

/// <summary>Segunda confirmação: o usuário decide commit ou rollback do que foi executado com as travas.</summary>
public sealed class GuardResolveHandler : MessageHandler<GuardResolveRequest, GuardResolveResponse>
{
    private readonly GuardedRunner _guard;
    private readonly TransactionNotifier _notifier;
    private readonly EventHub _hub;

    public GuardResolveHandler(GuardedRunner guard, TransactionNotifier notifier, EventHub hub)
    {
        _guard = guard;
        _notifier = notifier;
        _hub = hub;
        // Prazo de 120 s: o rollback automático já foi feito quando isto é chamado.
        _guard.Expired = e =>
        {
            _hub.Publish("query.message", new QueryMessageEvent(e.TabId, e.ExecutionId, MessageKinds.Info, e.Message, null));
            _hub.Publish("query.guardExpired", new GuardExpiredEvent(e.TabId, e.ExecutionId, e.Message));
            _ = _notifier.RefreshAsync(e.TabId);
        };
    }

    public override string Type => "query.guard.resolve";

    protected override async Task<GuardResolveResponse> HandleAsync(GuardResolveRequest r, CancellationToken ct)
    {
        GuardResolution result;
        try { result = await _guard.ResolveAsync(r.TabId, r.GuardId, r.Commit, ct); }
        catch (GuardExpiredException ex) { throw new BridgeException("guard_expired", ex.Message); }
        catch (TabNotConnectedException ex) { throw new BridgeException("not_connected", ex.Message); }

        _hub.Publish("query.message", new QueryMessageEvent(r.TabId, result.ExecutionId, MessageKinds.Info, result.Message, null));
        await _notifier.RefreshAsync(r.TabId);
        return new GuardResolveResponse(result.Committed, result.Message);
    }
}

/// <summary>Base dos comandos de transação da aba (iniciar, commit, rollback), recusados com execução ou decisão em andamento.</summary>
public abstract class TranCommandHandler(QueryRunner runner, GuardedRunner guard, TransactionNotifier notifier)
    : MessageHandler<TabIdRequest, TranResponse>
{
    protected abstract Task<int> RunAsync(string tabId, CancellationToken ct);

    protected override async Task<TranResponse> HandleAsync(TabIdRequest r, CancellationToken ct)
    {
        if (runner.IsRunning(r.TabId)) throw new BridgeException("busy", "Esta aba está executando um comando.");
        if (guard.HasPending(r.TabId)) throw new BridgeException("guard_pending", "Há uma decisão de commit/rollback pendente nesta aba.");
        try
        {
            var n = await RunAsync(r.TabId, ct);
            notifier.Publish(r.TabId, n);
            return new TranResponse(n);
        }
        catch (TabNotConnectedException ex) { throw new BridgeException("not_connected", ex.Message); }
        catch (System.Data.Common.DbException ex) { throw new BridgeException("sql_error", ex.Message); }
    }
}

public sealed class BeginTranHandler(QueryRunner runner, GuardedRunner guard, TransactionNotifier notifier, TransactionService tran)
    : TranCommandHandler(runner, guard, notifier)
{
    public override string Type => "tran.begin";
    protected override Task<int> RunAsync(string tabId, CancellationToken ct) => tran.BeginAsync(tabId, ct);
}

public sealed class CommitTranHandler(QueryRunner runner, GuardedRunner guard, TransactionNotifier notifier, TransactionService tran)
    : TranCommandHandler(runner, guard, notifier)
{
    public override string Type => "tran.commit";
    protected override Task<int> RunAsync(string tabId, CancellationToken ct) => tran.CommitAsync(tabId, ct);
}

public sealed class RollbackTranHandler(QueryRunner runner, GuardedRunner guard, TransactionNotifier notifier, TransactionService tran)
    : TranCommandHandler(runner, guard, notifier)
{
    public override string Type => "tran.rollback";
    protected override Task<int> RunAsync(string tabId, CancellationToken ct) => tran.RollbackAsync(tabId, ct);
}

/// <summary>Fecha o app depois que o frontend resolveu as transações abertas (ver <c>MainWindow.OnClosing</c>).</summary>
public sealed class ForceCloseHandler(WindowController win) : MessageHandler<EmptyRequest, EmptyResponse>
{
    public override string Type => "window.forceClose";

    protected override Task<EmptyResponse> HandleAsync(EmptyRequest r, CancellationToken ct)
    {
        win.ForceClose();
        return Task.FromResult(new EmptyResponse());
    }
}
