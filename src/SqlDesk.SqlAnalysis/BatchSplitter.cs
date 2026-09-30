namespace SqlDesk.SqlAnalysis;

public static class BatchSplitter
{
    /// <summary>
    /// Separa o script em batches pelas linhas <c>GO</c> (que não é T-SQL: é comando do sqlcmd/SSMS).
    /// Ignora <c>GO</c> dentro de strings e comentários. Batches só com espaço em branco são descartados.
    /// <c>GO n</c> fica em <see cref="Batch.RepeatCount"/> do batch que o antecede.
    /// </summary>
    public static IReadOnlyList<Batch> Split(string script)
    {
        var scanner = new SqlLexicalScanner(script);
        var map = new LineMap(script);
        var batches = new List<Batch>();
        var batchStart = 0;

        bool Close(int end, int repeat)
        {
            if (end <= batchStart || script.AsSpan(batchStart, end - batchStart).IsWhiteSpace()) return false;
            batches.Add(new Batch(batches.Count, script[batchStart..end], batchStart, map.LineOf(batchStart), repeat));
            return true;
        }

        foreach (var line in scanner.Lines)
        {
            if (!line.IsGo) continue;
            Close(line.Start, line.GoCount);
            batchStart = line.NextStart;
        }
        Close(script.Length, 1);
        return batches;
    }
}
