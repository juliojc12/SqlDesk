using System.Collections.Concurrent;
using System.Data;
using MiniExcelLibs;
using MiniExcelLibs.Attributes;
using MiniExcelLibs.OpenXml;
using SqlDesk.Core.Execution;

namespace SqlDesk.Core.Export;

/// <summary>
/// XLSX em streaming com o MiniExcel: as linhas entram numa fila limitada e o MiniExcel as consome em outra thread,
/// então a memória não cresce com o tamanho do resultado. Tipos preservados (números, datas, bit).
/// </summary>
public sealed class XlsxRowWriter(Stream stream) : IRowWriter
{
    public const int MaxDataRows = 1_048_575;

    private BlockingCollection<Dictionary<string, object?>>? _queue;
    private Task? _consumer;
    private string[] _keys = [];
    private IReadOnlyList<ColumnInfo> _columns = [];

    public long Rows { get; private set; }

    public void Begin(IReadOnlyList<ColumnInfo> columns)
    {
        _columns = columns;
        _keys = UniqueHeaders(columns.Select(c => c.Name));
        _queue = new BlockingCollection<Dictionary<string, object?>>(boundedCapacity: 1000);
        var queue = _queue;
        var keys = _keys;
        var configuration = new OpenXmlConfiguration
        {
            EnableAutoWidth = false,
            TableStyles = TableStyles.None, // o estilo padrão do MiniExcel deixa o cabeçalho branco sobre fundo quase transparente
            FreezeRowCount = 1,
            DynamicColumns = DateFormats(columns, keys),
        };
        _consumer = Task.Run(() =>
        {
            // Com linhas: dicionários em streaming. Sem linhas: uma tabela vazia só para escrever o cabeçalho.
            var first = queue.GetConsumingEnumerable().GetEnumerator();
            object data = first.MoveNext()
                ? Prepend(first)
                : EmptyTable(keys);
            stream.SaveAs(data, printHeader: true, sheetName: "Resultado", excelType: ExcelType.XLSX, configuration: configuration);
        });
    }

    /// <summary>Datas com formato de data (e hora, quando o tipo tem hora): sem isto o MiniExcel mostra só o dia.</summary>
    private static DynamicExcelColumn[] DateFormats(IReadOnlyList<ColumnInfo> columns, string[] keys) =>
        columns.Select((c, i) => (c, i))
            .Where(x => x.c.Kind == "date" && !x.c.TypeName.Equals("datetimeoffset", StringComparison.OrdinalIgnoreCase))
            .Select(x => new DynamicExcelColumn(keys[x.i]) { Format = x.c.TypeName.Equals("date", StringComparison.OrdinalIgnoreCase) ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm:ss" })
            .ToArray();

    private static IEnumerable<IDictionary<string, object?>> Prepend(IEnumerator<Dictionary<string, object?>> e)
    {
        do yield return e.Current;
        while (e.MoveNext());
    }

    private static DataTable EmptyTable(string[] keys)
    {
        var t = new DataTable();
        foreach (var k in keys) t.Columns.Add(k, typeof(string));
        return t;
    }

    public void Write(object?[] row, CancellationToken ct)
    {
        if (Rows >= MaxDataRows)
            throw new ExportLimitException("O XLSX comporta no máximo 1.048.575 linhas de dados. Exporte em CSV.");
        var cells = new Dictionary<string, object?>(_keys.Length);
        for (var i = 0; i < _keys.Length; i++) cells[_keys[i]] = ExportValues.ToCell(i < row.Length ? row[i] : null, _columns[i].TypeName);

        // Se o consumidor falhou (disco cheio...), não adianta continuar enchendo a fila.
        while (!_queue!.TryAdd(cells, 200, ct))
            if (_consumer!.IsFaulted) _consumer.GetAwaiter().GetResult();
        Rows++;
    }

    public void Complete()
    {
        _queue!.CompleteAdding();
        _consumer!.GetAwaiter().GetResult();
        if (stream.CanSeek && stream.CanRead && stream.CanWrite) XlsxStyler.BoldHeader(stream);
    }

    public void Dispose()
    {
        try { _queue?.CompleteAdding(); } catch (ObjectDisposedException) { }
        try { _consumer?.Wait(5000); } catch { /* o erro já foi (ou será) reportado por Complete */ }
    }

    /// <summary>Nomes de coluna repetidos ou vazios viram "nome", "nome_2"... (o XLSX os usa como chaves).</summary>
    public static string[] UniqueHeaders(IEnumerable<string> names)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return names.Select((n, i) =>
        {
            var baseName = string.IsNullOrWhiteSpace(n) ? $"coluna{i + 1}" : n;
            if (!seen.TryGetValue(baseName, out var count))
            {
                seen[baseName] = 1;
                return baseName;
            }
            seen[baseName] = ++count;
            return $"{baseName}_{count}";
        }).ToArray();
    }
}
