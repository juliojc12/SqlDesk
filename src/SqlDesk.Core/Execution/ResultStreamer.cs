using System.Data.Common;
using System.Diagnostics;

namespace SqlDesk.Core.Execution;

/// <summary>Lê o result set corrente de um <see cref="DbDataReader"/> e o entrega ao <see cref="IExecutionSink"/> em lotes.</summary>
public static class ResultStreamer
{
    public const int BatchSize = 500;
    public const int DefaultMaxRows = 10_000;

    /// <summary>Se um lote demora para encher, é enviado assim mesmo: as primeiras linhas aparecem logo.</summary>
    private static readonly TimeSpan MaxBatchAge = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Devolve false (sem emitir nada) quando o result set não tem colunas, como o de um INSERT. Com
    /// <paramref name="maxRows"/>, para de ler ao atingir o limite e marca o resultado como truncado; o resto do
    /// result set é descartado pelo <c>NextResult</c>.
    /// </summary>
    public static async Task<(bool Emitted, long RowCount, bool Truncated)> StreamCurrentAsync(
        DbDataReader reader, int resultIndex, DocRange source, IExecutionSink sink, int? maxRows, CancellationToken ct)
    {
        var fieldCount = reader.FieldCount;
        if (fieldCount == 0) return (false, 0, false);

        var typeNames = new string[fieldCount];
        var columns = new ColumnInfo[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            typeNames[i] = reader.GetDataTypeName(i);
            columns[i] = new ColumnInfo(reader.GetName(i), CellValues.KindOf(reader.GetFieldType(i)), typeNames[i]);
        }
        sink.ResultStarted(resultIndex, source, columns);

        var raw = new object[fieldCount];
        var batch = new List<object?[]>(BatchSize);
        var age = Stopwatch.StartNew();
        long count = 0;
        var truncated = false;

        while (await reader.ReadAsync(ct))
        {
            if (maxRows is { } limit && count >= limit)
            {
                truncated = true;
                break;
            }

            reader.GetValues(raw);
            var row = new object?[fieldCount];
            for (var i = 0; i < fieldCount; i++) row[i] = CellValues.Convert(raw[i], typeNames[i]);
            batch.Add(row);
            count++;

            if (batch.Count >= BatchSize || (batch.Count > 0 && age.Elapsed >= MaxBatchAge))
            {
                sink.Rows(resultIndex, batch);
                batch = new List<object?[]>(BatchSize);
                age.Restart();
            }
        }

        if (batch.Count > 0) sink.Rows(resultIndex, batch);
        sink.ResultCompleted(resultIndex, count, truncated);
        return (true, count, truncated);
    }
}
