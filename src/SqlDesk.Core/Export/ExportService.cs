using System.Diagnostics;
using SqlDesk.Core.Execution;
using SqlDesk.SqlAnalysis;

namespace SqlDesk.Core.Export;

/// <summary>A exportação não pôde ser feita; a mensagem é para o usuário.</summary>
public sealed class ExportFailedException(string message) : Exception(message);

/// <summary>Recebe as linhas de um result set direto do leitor e as entrega a um <see cref="IRowWriter"/>, na ordem de colunas pedida.</summary>
public sealed class ExportSink(IRowWriter writer, int resultOrdinal, IReadOnlyList<int>? columnOrder, Action<long>? progress, CancellationToken ct) : IExecutionSink
{
    private const int ProgressEvery = 5_000;

    private int _currentIndex = -1;
    private int[]? _order;

    public bool WantsRawValues => true;

    /// <summary>O result set pedido foi encontrado (e o cabeçalho escrito).</summary>
    public bool Started { get; private set; }

    /// <summary>Primeira mensagem de erro do SQL Server, para explicar uma falha.</summary>
    public string? FirstError { get; private set; }

    private int _seen;

    public void ResultStarted(int resultIndex, DocRange source, IReadOnlyList<ColumnInfo> columns)
    {
        // "resultOrdinal" é a posição do result set entre os que o texto produz (o texto pode ter vários).
        if (_seen++ != resultOrdinal) return;
        _currentIndex = resultIndex;
        _order = columnOrder is { Count: > 0 } o && o.All(i => i >= 0 && i < columns.Count) ? [.. o] : Enumerable.Range(0, columns.Count).ToArray();
        writer.Begin(_order.Select(i => columns[i]).ToList());
        Started = true;
    }

    public void Rows(int resultIndex, IReadOnlyList<object?[]> rows)
    {
        if (resultIndex != _currentIndex || _order is null) return;
        foreach (var row in rows)
        {
            var values = new object?[_order.Length];
            for (var i = 0; i < _order.Length; i++) values[i] = row[_order[i]];
            writer.Write(values, ct);
            if (writer.Rows % ProgressEvery == 0) progress?.Invoke(writer.Rows);
        }
    }

    public void ResultCompleted(int resultIndex, long rowCount, bool truncated) { }

    public void Message(string kind, string text, int? line)
    {
        if (kind == MessageKinds.Error) FirstError ??= text;
    }
}

public static class ExportService
{
    public static IRowWriter CreateWriter(ExportFormat format, Stream stream, char delimiter) =>
        format == ExportFormat.Xlsx ? new XlsxRowWriter(stream) : new CsvRowWriter(stream, delimiter);

    private static string PartPath(string path) => path + ".sqldesk-part";

    /// <summary>Escreve em um arquivo temporário ao lado do destino e só o move para o nome final no sucesso; falha ou cancelamento não deixam arquivo pela metade.</summary>
    private static async Task<ExportResult> WriteAtomicAsync(string path, Func<Stream, IRowWriter> makeWriter, Func<IRowWriter, Task> fill)
    {
        var watch = Stopwatch.StartNew();
        var part = PartPath(path);
        try
        {
            long rows;
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 64 * 1024))
            using (var writer = makeWriter(fs))
            {
                await fill(writer);
                writer.Complete();
                rows = writer.Rows;
            }
            File.Move(part, path, overwrite: true);
            return new ExportResult(path, rows, watch.ElapsedMilliseconds);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { /* arquivo em uso: o próximo export sobrescreve */ }
        }
    }

    /// <summary>
    /// Exporta linhas que o frontend já tem (resultado carregado), na ordem de linhas e colunas que ele enviou. Valores que passaram
    /// pelo JSON (decimal e bigint como texto, datas em ISO) voltam ao tipo original para o XLSX preservar números e datas.
    /// </summary>
    public static Task<ExportResult> ExportLoadedAsync(
        ExportFormat format, string path, char delimiter, IReadOnlyList<ColumnInfo> columns, IEnumerable<object?[]> rows,
        Action<long>? progress, CancellationToken ct) =>
        WriteAtomicAsync(path, fs => CreateWriter(format, fs, delimiter), writer =>
        {
            writer.Begin(columns);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var typed = new object?[columns.Count];
                for (var i = 0; i < columns.Count; i++) typed[i] = ExportValues.Coerce(i < row.Length ? row[i] : null, columns[i]);
                writer.Write(typed, ct);
                if (writer.Rows % 5_000 == 0) progress?.Invoke(writer.Rows);
            }
            return Task.CompletedTask;
        });

    /// <summary>
    /// Reexecuta o texto na conexão da aba e grava o result set direto no arquivo, em streaming, sem o limite de linhas da grade.
    /// Só reexecuta texto que apenas lê dados: repetir um INSERT, um EXEC ou um SELECT ... INTO repetiria o efeito.
    /// </summary>
    public static Task<ExportResult> ExportByRerunAsync(
        IBatchRunner runner, string tabId, string sourceText, ExportFormat format, string path, char delimiter,
        int resultOrdinal, IReadOnlyList<int>? columnOrder, Action<long>? progress, CancellationToken ct) =>
        WriteAtomicAsync(path, fs => CreateWriter(format, fs, delimiter), async writer =>
        {
            if (ExecutionPlanner.Plan(sourceText, 0, 0, sourceText.Length, wholeScript: true) is not ExecutionPlan.Runnable plan)
                throw new ExportFailedException("O texto contém comandos que exigem confirmação (UPDATE/DELETE sem WHERE, TRUNCATE ou DROP) e não será reexecutado para exportar.");
            if (!ReadOnlyAnalyzer.IsReadOnly(sourceText, out var reason))
                throw new ExportFailedException($"Não é seguro reexecutar este texto para exportar: {reason}. Exporte só as linhas carregadas.");

            var sink = new ExportSink(writer, resultOrdinal, columnOrder, progress, ct);
            var summary = await runner.RunAsync(tabId, plan.Batches, plan.BaseOffset, plan.BaseLine, maxRows: null, sink, ct);

            if (summary.Status == RunStatus.Cancelled) throw new OperationCanceledException(ct);
            if (summary.Status != RunStatus.Completed)
                throw new ExportFailedException(sink.FirstError ?? "A execução falhou durante a exportação.");
            if (!sink.Started)
                throw new ExportFailedException("O texto não produziu o resultado a exportar (o resultado pode ter mudado desde a execução original).");
        });
}
