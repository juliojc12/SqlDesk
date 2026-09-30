namespace SqlDesk.Host.Bridge;

// Espelhado em SqlDesk.Web/src/contracts.ts. Definir cada tipo uma única vez em cada lado.

public sealed record BridgeError(string Code, string Message, int? Line = null);

public sealed record PingRequest(string Message);

public sealed record PingResponse(string Message, DateTimeOffset ServerTime);

// ---- Conexões ----
public sealed record EmptyRequest;

public sealed record EmptyResponse;

public sealed record ConnectionListResponse(IReadOnlyList<SqlDesk.Core.Connections.ConnectionInfo> Connections);

public sealed record ConnectionIdRequest(Guid Id);

public sealed record TestConnectionRequest(Guid? Id, SqlDesk.Core.Connections.ConnectionSettings Settings, string? Password);

public sealed record ParseConnectionStringRequest(string ConnectionString);

public sealed record ParseConnectionStringResponse(SqlDesk.Core.Connections.ConnectionSettings Settings, string? Password);

public sealed record BuildConnectionStringRequest(SqlDesk.Core.Connections.ConnectionSettings Settings, string? Password);

public sealed record BuildConnectionStringResponse(string ConnectionString);
