using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using SqlDesk.Core.Execution;

namespace SqlDesk.Core.Export;

/// <summary>CSV em UTF-8 com BOM (o Excel abre os acentos certos), separador configurável, datas ISO 8601.</summary>
public sealed class CsvRowWriter(Stream stream, char delimiter = ';') : IRowWriter
{
    private StreamWriter? _text;
    private CsvWriter? _csv;
    private IReadOnlyList<ColumnInfo> _columns = [];

    public long Rows { get; private set; }

    public void Begin(IReadOnlyList<ColumnInfo> columns)
    {
        _columns = columns;
        _text = new StreamWriter(stream, new UTF8Encoding(true), 64 * 1024, leaveOpen: true);
        _csv = new CsvWriter(_text, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter.ToString(),
            NewLine = "\r\n",
            // O padrão do CsvHelper só cita campos com a quebra de linha configurada ("\r\n"); um "\n" ou "\r" solto
            // dentro do campo quebraria a linha ao abrir no Excel.
            ShouldQuote = args =>
            {
                var f = args.Field;
                return !string.IsNullOrEmpty(f)
                    && (f.Contains(delimiter) || f.Contains('"') || f.Contains('\n') || f.Contains('\r') || f[0] == ' ' || f[^1] == ' ');
            },
        });
        foreach (var c in columns) _csv.WriteField(c.Name);
        _csv.NextRecord();
    }

    public void Write(object?[] row, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        for (var i = 0; i < _columns.Count; i++) _csv!.WriteField(ExportValues.ToText(i < row.Length ? row[i] : null, _columns[i].TypeName));
        _csv!.NextRecord();
        Rows++;
    }

    public void Complete()
    {
        _csv?.Flush();
        _text?.Flush();
    }

    public void Dispose()
    {
        _csv?.Dispose();
        _text?.Dispose();
    }
}
