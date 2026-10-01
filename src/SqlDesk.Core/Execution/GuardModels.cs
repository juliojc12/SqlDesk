using SqlDesk.SqlAnalysis;

namespace SqlDesk.Core.Execution;

/// <summary>
/// O que a segunda confirmação mostra para cada statement perigoso: quantas linhas foram afetadas e, para
/// UPDATE/DELETE, uma amostra do antes e do depois. <see cref="Line"/> é a linha (base 1) no documento.
/// </summary>
public sealed record GuardChange(
    DangerKind Kind,
    string? Target,
    int Line,
    string Description,
    long? AffectedRows,
    bool HasPreview,
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Before,
    IReadOnlyList<object?[]> After);

public sealed record GuardInfo(
    string GuardId,
    int TimeoutSeconds,
    bool UsesSavepoint,
    /// <summary>O OUTPUT falhou (erro 334, tabela com trigger): executado sem amostra do antes e depois.</summary>
    bool PreviewUnavailable,
    /// <summary>Não foi possível ligar cada result set ao seu statement (IF, loop, SELECTs no meio): sem amostras.</summary>
    bool Approximate,
    /// <summary>Soma das linhas afetadas por statements sem result set (DML puro).</summary>
    long TotalAffected,
    IReadOnlyList<GuardChange> Changes);

public static class GuardStatus
{
    public const string PendingDecision = "pending_decision";

    /// <summary>A transação deixou de existir durante o script (COMMIT/ROLLBACK dentro dele): não há mais o que desfazer.</summary>
    public const string TransactionLost = "tran_lost";
}

public sealed record GuardOutcome(string Status, long ElapsedMs, long TotalRows, GuardInfo? Pending);

public sealed record GuardResolution(string ExecutionId, bool Committed, string Message);

public sealed record GuardExpired(string TabId, string ExecutionId, string Message);

public sealed class GuardPendingException(string message) : Exception(message);

public sealed class GuardExpiredException(string message) : Exception(message);

/// <summary>Result set capturado durante a execução com travas (amostra de até <see cref="CapturingSink.SampleSize"/> linhas).</summary>
public sealed class CapturedSet(int index, IReadOnlyList<ColumnInfo> columns)
{
    public int Index { get; } = index;
    public IReadOnlyList<ColumnInfo> Columns { get; } = columns;
    public List<object?[]> Sample { get; } = [];
    public long RowCount { get; set; }

    /// <summary>Linhas reportadas pelo SQL Server para o statement que gerou o result set.</summary>
    public long? Affected { get; set; }
}

/// <summary>Repassa tudo ao destino real e, ao mesmo tempo, guarda o que a segunda confirmação precisa.</summary>
public sealed class CapturingSink(IExecutionSink inner) : IExecutionSink
{
    public const int SampleSize = 200;

    private readonly Dictionary<int, CapturedSet> _byIndex = [];
    private CapturedSet? _awaitingCount;

    public List<CapturedSet> Sets { get; } = [];

    public long TotalAffected { get; private set; }

    public void ResultStarted(int resultIndex, DocRange source, IReadOnlyList<ColumnInfo> columns)
    {
        var set = new CapturedSet(resultIndex, columns);
        _byIndex[resultIndex] = set;
        Sets.Add(set);
        inner.ResultStarted(resultIndex, source, columns);
    }

    public void Rows(int resultIndex, IReadOnlyList<object?[]> rows)
    {
        if (_byIndex.TryGetValue(resultIndex, out var set))
        {
            var room = SampleSize - set.Sample.Count;
            if (room > 0) set.Sample.AddRange(rows.Take(room));
        }
        inner.Rows(resultIndex, rows);
    }

    public void ResultCompleted(int resultIndex, long rowCount, bool truncated)
    {
        if (_byIndex.TryGetValue(resultIndex, out var set))
        {
            set.RowCount = rowCount;
            _awaitingCount = set;
        }
        inner.ResultCompleted(resultIndex, rowCount, truncated);
    }

    public void Message(string kind, string text, int? line) => inner.Message(kind, text, line);

    public void StatementCompleted(long recordCount)
    {
        // O DONE de um statement chega logo depois das linhas do seu result set (OUTPUT ou SELECT).
        if (_awaitingCount is { } set)
        {
            set.Affected = recordCount;
            _awaitingCount = null;
        }
        else
        {
            TotalAffected += recordCount;
        }
        inner.StatementCompleted(recordCount);
    }
}

/// <summary>Liga os result sets capturados (OUTPUT) aos statements perigosos e monta o conteúdo da segunda confirmação.</summary>
public static class GuardReportBuilder
{
    /// <param name="counts">Contagem prévia (<c>COUNT_BIG</c>) por statement, indexada por <see cref="DangerousStatement.Start"/>.</param>
    /// <param name="baseLine">Linha (base 1) do documento onde o texto analisado começa.</param>
    public static (IReadOnlyList<GuardChange> Changes, bool Approximate) Build(
        IReadOnlyList<DangerousStatement> dangers,
        RewriteResult rewrite,
        IReadOnlyList<CapturedSet> sets,
        IReadOnlyDictionary<int, long?> counts,
        bool previewUnavailable,
        int baseLine)
    {
        // Cada UPDATE/DELETE reescrito devolve um result set, na ordem do documento. Só dá para ligar um ao outro
        // quando a quantidade bate; com IF, loops ou SELECTs no meio a correspondência é incerta.
        var expected = rewrite.Rewritten.Count;
        var exact = !previewUnavailable && expected > 0 && sets.Count == expected;
        var approximate = !previewUnavailable && expected > 0 && !exact;
        var order = rewrite.Rewritten.Select(r => r.OriginalStart).ToList();

        var changes = new List<GuardChange>();
        foreach (var d in dangers.OrderBy(d => d.Start))
        {
            var line = baseLine - 1 + d.Line;
            var idx = order.IndexOf(d.Start);

            if (exact && idx >= 0 && Preview(d, sets[idx]) is { } preview)
            {
                changes.Add(new GuardChange(d.Kind, d.Target, line, d.Description, sets[idx].Affected ?? sets[idx].RowCount,
                    true, preview.Columns, preview.Before, preview.After));
                continue;
            }

            counts.TryGetValue(d.Start, out var count);
            changes.Add(new GuardChange(d.Kind, d.Target, line, d.Description, count, false, [], [], []));
        }
        return (changes, approximate);
    }

    private static (IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Before, IReadOnlyList<object?[]> After)? Preview(
        DangerousStatement d, CapturedSet set)
    {
        var names = set.Columns.Select(c => c.Name).ToList();
        if (d.Kind == DangerKind.DeleteWithoutWhere)
            return (names, set.Sample, []);

        // UPDATE: OUTPUT deleted.*, inserted.* devolve as colunas da tabela duas vezes (antes, depois).
        if (d.Kind != DangerKind.UpdateWithoutWhere || names.Count == 0 || names.Count % 2 != 0) return null;
        var n = names.Count / 2;
        return (names.Take(n).ToList(),
            set.Sample.Select(r => r.Take(n).ToArray()).ToList(),
            set.Sample.Select(r => r.Skip(n).ToArray()).ToList());
    }
}
