namespace SqlDesk.Core.Execution;

/// <summary>Categoria de coluna usada pelo frontend para ordenar e alinhar: number, text, date, bool ou binary.</summary>
public sealed record ColumnInfo(string Name, string Kind, string TypeName);

public static class MessageKinds
{
    public const string Info = "info";
    public const string Rows = "rows";
    public const string Error = "error";
    public const string Timing = "timing";
}

public static class RunStatus
{
    public const string Completed = "completed";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
}

/// <summary>Intervalo no documento original (offset base 0 e comprimento em caracteres).</summary>
public sealed record DocRange(int Start, int Length);

/// <summary>
/// Destino dos eventos de uma execução. As chamadas vêm da thread que lê o <c>SqlDataReader</c>, na ordem em que
/// os fatos acontecem; a implementação precisa ser segura para uso a partir de qualquer thread.
/// </summary>
public interface IExecutionSink
{
    /// <param name="resultIndex">Índice do result set na execução (0, 1, 2...), contínuo entre os batches.</param>
    /// <param name="source">Trecho do documento que gerou o result set (o batch).</param>
    void ResultStarted(int resultIndex, DocRange source, IReadOnlyList<ColumnInfo> columns);

    void Rows(int resultIndex, IReadOnlyList<object?[]> rows);

    void ResultCompleted(int resultIndex, long rowCount, bool truncated);

    /// <param name="line">Linha (base 1) no documento, quando conhecida.</param>
    void Message(string kind, string text, int? line);
}

public sealed record RunSummary(string Status, long ElapsedMs, long TotalRows);
