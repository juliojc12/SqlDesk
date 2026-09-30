namespace SqlDesk.Host.Bridge;

// Espelhado em SqlDesk.Web/src/contracts.ts. Definir cada tipo uma única vez em cada lado.

public sealed record BridgeError(string Code, string Message, int? Line = null);

public sealed record PingRequest(string Message);

public sealed record PingResponse(string Message, DateTimeOffset ServerTime);
