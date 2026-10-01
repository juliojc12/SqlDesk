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
    string Mode, bool NoRowLimit, bool ConfirmDangerous = false, bool SkipTranAdvice = false, int? MaxRows = null);

public sealed record BlockedStatement(int Line, string Description);

/// <param name="Status">
/// completed | error | cancelled | refused | nothing | needs_confirmation (primeira confirmação: nada foi executado) |
/// advise_transaction (barra de recomendação: nada foi executado) | pending_decision (segunda confirmação: executado
/// dentro de transação, aguardando commit ou rollback) | tran_lost. Não usar o nome "error" para campos: colide com o envelope de erro.
/// </param>
/// <param name="Range">Trecho do documento que seria/foi executado (para "Envolver em transação").</param>
public sealed record ExecuteResponse(
    string Status, long ElapsedMs, long TotalRows, string? Message, IReadOnlyList<BlockedStatement>? Blocked,
    SqlDesk.Core.Execution.DocRange? Range = null, SqlDesk.Core.Execution.GuardInfo? Guard = null);

public sealed record GuardResolveRequest(string TabId, string GuardId, bool Commit);

public sealed record GuardResolveResponse(bool Committed, string Message);

public sealed record TranResponse(int TranCount);

// ---- Transações (eventos) ----
public sealed record TabTransactionEvent(string TabId, int Count);

public sealed record TabConnectionLostEvent(string TabId, bool HadTransaction);

public sealed record GuardExpiredEvent(string TabId, string ExecutionId, string Message);

public sealed record OpenTransactionTab(string TabId, int Count);

public sealed record AppCloseRequestedEvent(IReadOnlyList<OpenTransactionTab> Tabs);

public sealed record QueryStartedEvent(string TabId, string ExecutionId, SqlDesk.Core.Execution.DocRange? Range);

public sealed record QueryResultStartedEvent(
    string TabId, string ExecutionId, int ResultIndex, SqlDesk.Core.Execution.DocRange Source,
    IReadOnlyList<SqlDesk.Core.Execution.ColumnInfo> Columns);

public sealed record QueryRowsEvent(string TabId, string ExecutionId, int ResultIndex, IReadOnlyList<object?[]> Rows);

public sealed record QueryResultCompletedEvent(string TabId, string ExecutionId, int ResultIndex, long RowCount, bool Truncated);

public sealed record QueryMessageEvent(string TabId, string ExecutionId, string Kind, string Text, int? Line);

// ---- Metadados (autocomplete e árvore de objetos) ----
public sealed record MetadataRefreshRequest(Guid ConnectionId, bool Force);

public sealed record MetadataRefreshResponse(bool Started, bool Loading, bool Loaded);

public sealed record MetadataGetRequest(Guid ConnectionId);

public sealed record MetadataDto(
    bool Loaded, bool ColumnsLoaded, bool Loading,
    IReadOnlyList<string> Schemas,
    IReadOnlyList<SqlDesk.Core.Metadata.MetaObject> Objects,
    IReadOnlyDictionary<string, IReadOnlyList<SqlDesk.Core.Metadata.MetaColumn>> Columns);

/// <param name="Phase">objects | columns | error</param>
public sealed record MetadataUpdatedEvent(Guid ConnectionId, string Phase, string? Message);

// ---- Exportação ----
/// <param name="Format">csv | xlsx</param>
public sealed record ExportPickRequest(string Format, string SuggestedName);

public sealed record ExportPickResponse(bool Cancelled, string? Path);

/// <param name="Delimiter">Separador do CSV: ";" (padrão), "," ou tab.</param>
/// <param name="Rows">Linhas já na ordem exibida e com as colunas na ordem exibida (o frontend aplica ordenação e reordenação).</param>
public sealed record ExportLoadedRequest(
    string ExportId, string Format, string Path, string Delimiter,
    IReadOnlyList<SqlDesk.Core.Execution.ColumnInfo> Columns, System.Text.Json.JsonElement[][] Rows);

/// <param name="ResultOrdinal">Posição do result set entre os que o texto produz.</param>
/// <param name="ColumnOrder">Índices das colunas originais, na ordem exibida.</param>
public sealed record ExportRerunRequest(
    string ExportId, string TabId, string SourceText, string Format, string Path, string Delimiter,
    int ResultOrdinal, int[]? ColumnOrder);

public sealed record ExportDoneResponse(string Path, long Rows, long ElapsedMs);

public sealed record ExportCancelRequest(string ExportId);

public sealed record ExportProgressEvent(string ExportId, long Rows);

public sealed record PathRequest(string Path);

// ---- Janela ----
public sealed record WindowStateEvent(bool Maximized);
