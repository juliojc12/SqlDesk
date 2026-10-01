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

// ---- Abas / sessões ----
public sealed record OpenTabRequest(string TabId, Guid ConnectionId, string? Password);

public sealed record TabIdRequest(string TabId);

public sealed record SessionStateRequest(string State);

public sealed record SessionStateResponse(string? State);

// ---- Arquivos .sql ----
public sealed record SaveFileRequest(string? Path, string SuggestedName, string Content, bool SaveAs);

public sealed record SaveFileResponse(bool Cancelled, string? Path, string? Name);

public sealed record OpenFileResponse(bool Cancelled, string? Path, string? Name, string? Content);

// ---- Execução ----
// Eventos (id nulo): query.started, query.resultStarted, query.rows, query.resultCompleted, query.message.
// A resposta de query.execute chega depois de todos os eventos da execução.

/// <param name="Mode">"current" (seleção ou statement sob o cursor) ou "script" (documento inteiro).</param>
public sealed record ExecuteRequest(
    string TabId, string ExecutionId, string Text, int Cursor, int SelectionStart, int SelectionEnd,
    string Mode, bool NoRowLimit);

/// <param name="Status">completed | error | cancelled | refused | nothing. Não usar o nome "error" para campos: colide com o envelope de erro.</param>
public sealed record BlockedStatement(int Line, string Description);

public sealed record ExecuteResponse(
    string Status, long ElapsedMs, long TotalRows, string? Message, IReadOnlyList<BlockedStatement>? Blocked);

public sealed record QueryStartedEvent(string TabId, string ExecutionId, SqlDesk.Core.Execution.DocRange? Range);

public sealed record QueryResultStartedEvent(
    string TabId, string ExecutionId, int ResultIndex, SqlDesk.Core.Execution.DocRange Source,
    IReadOnlyList<SqlDesk.Core.Execution.ColumnInfo> Columns);

public sealed record QueryRowsEvent(string TabId, string ExecutionId, int ResultIndex, IReadOnlyList<object?[]> Rows);

public sealed record QueryResultCompletedEvent(string TabId, string ExecutionId, int ResultIndex, long RowCount, bool Truncated);

public sealed record QueryMessageEvent(string TabId, string ExecutionId, string Kind, string Text, int? Line);

// ---- Janela ----
public sealed record WindowStateEvent(bool Maximized);
