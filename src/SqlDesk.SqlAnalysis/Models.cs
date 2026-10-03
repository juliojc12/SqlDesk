namespace SqlDesk.SqlAnalysis;

/// <summary>
/// Um batch do script (texto entre linhas <c>GO</c>). <see cref="Start"/> é o offset no documento original e
/// <see cref="StartLine"/> a linha (base 1) onde o batch começa; ambos servem para devolver erros em coordenadas
/// do documento, já que o SQL Server reporta a linha relativa ao batch.
/// </summary>
public sealed record Batch(int Index, string Text, int Start, int StartLine, int RepeatCount = 1)
{
    public int End => Start + Text.Length;
}

public enum DangerKind
{
    UpdateWithoutWhere,
    DeleteWithoutWhere,
    TruncateTable,
    Drop,
    DropColumn,
    /// <summary>ALTER que muda a estrutura do banco (no MySQL o DDL confirma sozinho e não tem volta).</summary>
    AlterTable,
    /// <summary>Não foi possível analisar (erro de sintaxe) e o trecho contém UPDATE/DELETE/TRUNCATE/DROP.</summary>
    Unanalyzable,
}

public static class DangerKinds
{
    /// <summary>Muda a estrutura do banco (TRUNCATE, DROP, DROP COLUMN, ALTER): no MySQL/MariaDB confirma sozinho.</summary>
    public static bool IsDdl(DangerKind k) =>
        k is DangerKind.TruncateTable or DangerKind.Drop or DangerKind.DropColumn or DangerKind.AlterTable;
}

/// <summary>
/// Um statement que exige a dupla confirmação. <see cref="Start"/>/<see cref="Length"/> são offsets no documento
/// analisado; <see cref="Line"/> é a linha (base 1) em que ele começa.
/// </summary>
public sealed record DangerousStatement(
    DangerKind Kind,
    int Start,
    int Length,
    int Line,
    string Description,
    string? Target,
    IReadOnlyList<string> CountQueries,
    bool CanPreviewWithOutput)
{
    public int End => Start + Length;
}

public sealed record AnalysisDiagnostic(string Message, int Line, int Column);

public sealed record ScriptAnalysis(
    IReadOnlyList<Batch> Batches,
    IReadOnlyList<DangerousStatement> Dangers,
    IReadOnlyList<AnalysisDiagnostic> SyntaxErrors,
    IReadOnlyList<string> Warnings,
    bool HasWrites = false)
{
    /// <summary>Nenhum statement exige confirmação.</summary>
    public bool IsSafe => Dangers.Count == 0;
}

public sealed record TextRange(int Start, int Length)
{
    public int End => Start + Length;
}

public sealed record LocateResult(TextRange? Range, string? Message, bool UsedFallback)
{
    public bool Found => Range is not null;
}

public sealed record RewrittenStatement(DangerKind Kind, string? Target, int OriginalStart, int OriginalLine, int BatchIndex);

public sealed record RewriteResult(
    IReadOnlyList<Batch> Batches,
    IReadOnlyList<RewrittenStatement> Rewritten,
    IReadOnlyList<DangerousStatement> NotRewritten);
