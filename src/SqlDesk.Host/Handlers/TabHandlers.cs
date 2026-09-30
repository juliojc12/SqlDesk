using SqlDesk.Core.Connections;
using SqlDesk.Core.Sessions;
using SqlDesk.Host.Bridge;

namespace SqlDesk.Host.Handlers;

public sealed class OpenTabHandler(TabSessionManager sessions) : MessageHandler<OpenTabRequest, OpenSessionResult>
{
    public override string Type => "tabs.open";

    protected override async Task<OpenSessionResult> HandleAsync(OpenTabRequest r, CancellationToken ct)
    {
        try { return await sessions.OpenAsync(r.TabId, r.ConnectionId, r.Password, ct); }
        catch (PasswordRequiredException ex) { throw new BridgeException("password_required", ex.Message); }
        catch (ConnectFailedException ex) { throw new BridgeException("connect_failed", ex.Message); }
        catch (ConnectionValidationException ex) { throw new BridgeException("validation", ex.Message); }
    }
}

/// <summary>Fecha a conexão da aba (a aba e o texto continuam existindo). Também usado ao fechar a aba.</summary>
public sealed class DisconnectTabHandler(TabSessionManager sessions) : MessageHandler<TabIdRequest, EmptyResponse>
{
    public override string Type => "tabs.disconnect";

    protected override async Task<EmptyResponse> HandleAsync(TabIdRequest r, CancellationToken ct)
    {
        await sessions.DisconnectAsync(r.TabId);
        return new EmptyResponse();
    }
}

public sealed class DisconnectConnectionHandler(TabSessionManager sessions) : MessageHandler<ConnectionIdRequest, EmptyResponse>
{
    public override string Type => "connections.disconnect";

    protected override async Task<EmptyResponse> HandleAsync(ConnectionIdRequest r, CancellationToken ct)
    {
        await sessions.DisconnectConnectionAsync(r.Id);
        return new EmptyResponse();
    }
}

public sealed class LoadSessionStateHandler(SessionStateStore store) : MessageHandler<EmptyRequest, SessionStateResponse>
{
    public override string Type => "session.load";

    protected override Task<SessionStateResponse> HandleAsync(EmptyRequest r, CancellationToken ct) =>
        Task.FromResult(new SessionStateResponse(store.Load()));
}

public sealed class SaveSessionStateHandler(SessionStateStore store) : MessageHandler<SessionStateRequest, EmptyResponse>
{
    public override string Type => "session.save";

    protected override Task<EmptyResponse> HandleAsync(SessionStateRequest r, CancellationToken ct)
    {
        store.Save(r.State);
        return Task.FromResult(new EmptyResponse());
    }
}
