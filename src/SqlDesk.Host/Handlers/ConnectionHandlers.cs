using System.IO;
using SqlDesk.Core.Connections;
using SqlDesk.Host.Bridge;

namespace SqlDesk.Host.Handlers;

// Erros de validação do Core viram BridgeException (código "validation") para o frontend exibir a mensagem.
internal static class Guard
{
    public static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (ConnectionValidationException ex) { throw new BridgeException("validation", ex.Message); }
        catch (InvalidDataException ex) { throw new BridgeException("storage_corrupt", ex.Message); }
    }

    public static Task<T> Run<T>(Func<T> action) => Run(() => Task.FromResult(action()));
}

public sealed class ListConnectionsHandler(ConnectionStore store) : MessageHandler<EmptyRequest, ConnectionListResponse>
{
    public override string Type => "connections.list";

    protected override Task<ConnectionListResponse> HandleAsync(EmptyRequest r, CancellationToken ct) =>
        Guard.Run(() => new ConnectionListResponse(store.List()));
}

public sealed class SaveConnectionHandler(ConnectionStore store) : MessageHandler<SaveConnectionRequest, ConnectionInfo>
{
    public override string Type => "connections.save";

    protected override Task<ConnectionInfo> HandleAsync(SaveConnectionRequest r, CancellationToken ct) =>
        Guard.Run(() => store.Save(r));
}

public sealed class DeleteConnectionHandler(ConnectionStore store) : MessageHandler<ConnectionIdRequest, EmptyResponse>
{
    public override string Type => "connections.delete";

    protected override Task<EmptyResponse> HandleAsync(ConnectionIdRequest r, CancellationToken ct) =>
        Guard.Run(() => { store.Delete(r.Id); return new EmptyResponse(); });
}

public sealed class DuplicateConnectionHandler(ConnectionStore store) : MessageHandler<ConnectionIdRequest, ConnectionInfo>
{
    public override string Type => "connections.duplicate";

    protected override Task<ConnectionInfo> HandleAsync(ConnectionIdRequest r, CancellationToken ct) =>
        Guard.Run(() => store.Duplicate(r.Id));
}

public sealed class TestConnectionHandler(ConnectionStore store) : MessageHandler<TestConnectionRequest, TestConnectionResult>
{
    public override string Type => "connections.test";

    protected override Task<TestConnectionResult> HandleAsync(TestConnectionRequest r, CancellationToken ct) =>
        Guard.Run(async () =>
        {
            // Senha em branco em conexão já salva: usa a senha salva (ela nunca passa pelo frontend).
            var password = !string.IsNullOrEmpty(r.Password) ? r.Password : r.Id is { } id ? store.GetPassword(id) : null;
            return await ConnectionTester.TestAsync(r.Settings, password, ct);
        });
}

public sealed class ParseConnectionStringHandler : MessageHandler<ParseConnectionStringRequest, ParseConnectionStringResponse>
{
    public override string Type => "connections.parse";

    protected override Task<ParseConnectionStringResponse> HandleAsync(ParseConnectionStringRequest r, CancellationToken ct) =>
        Guard.Run(() =>
        {
            var (settings, password) = ConnectionStringService.Parse(r.ConnectionString);
            return new ParseConnectionStringResponse(settings, password);
        });
}

public sealed class BuildConnectionStringHandler : MessageHandler<BuildConnectionStringRequest, BuildConnectionStringResponse>
{
    public override string Type => "connections.build";

    protected override Task<BuildConnectionStringResponse> HandleAsync(BuildConnectionStringRequest r, CancellationToken ct) =>
        Guard.Run(() => new BuildConnectionStringResponse(ConnectionStringService.Build(r.Settings, r.Password)));
}
