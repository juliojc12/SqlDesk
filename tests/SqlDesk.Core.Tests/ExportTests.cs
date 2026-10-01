using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using MiniExcelLibs;
using SqlDesk.Core.Execution;
using SqlDesk.Core.Export;

namespace SqlDesk.Core.Tests;

public class ExportTests
{
    private static readonly ColumnInfo[] Cols =
    [
        new("Id", "number", "int"), new("Nome", "text", "nvarchar"), new("Valor", "number", "decimal"),
        new("Criado", "date", "datetime2"), new("Ativo", "bool", "bit"), new("Obs", "text", "nvarchar"),
    ];

    private static object?[] Row(int i) =>
        [i, $"Nome; {i}", 1234.5m + i, new DateTime(2026, 9, 30, 13, 45, 10).AddDays(i), i % 2 == 0, i % 3 == 0 ? null : "linha\ncom \"aspas\""];

    private static byte[] Write(IRowWriter w, Stream ms, int rows)
    {
        using (w)
        {
            w.Begin(Cols);
            for (var i = 1; i <= rows; i++) w.Write(Row(i), default);
            w.Complete();
        }
        return ((MemoryStream)ms).ToArray();
    }

    // ---- valores ----

    [Fact]
    public void Texto_de_CSV_usa_ISO_8601_ponto_decimal_e_NULL_vazio()
    {
        Assert.Equal("", ExportValues.ToText(null));
        Assert.Equal("", ExportValues.ToText(DBNull.Value));
        Assert.Equal("2026-09-30T13:45:10", ExportValues.ToText(new DateTime(2026, 9, 30, 13, 45, 10), "datetime2"));
        Assert.Equal("2026-09-30T13:45:10.25", ExportValues.ToText(new DateTime(2026, 9, 30, 13, 45, 10, 250), "datetime"));
        Assert.Equal("2026-09-30", ExportValues.ToText(new DateTime(2026, 9, 30), "date"));
        Assert.Equal("2026-09-30T13:45:10-03:00", ExportValues.ToText(new DateTimeOffset(2026, 9, 30, 13, 45, 10, TimeSpan.FromHours(-3))));
        Assert.Equal("1234.50", ExportValues.ToText(1234.50m));
        Assert.Equal("0.1", ExportValues.ToText(0.1));
        Assert.Equal("1", ExportValues.ToText(true));
        Assert.Equal("0x0AFF", ExportValues.ToText(new byte[] { 0x0A, 0xFF }));
        Assert.Equal("12:30:00", ExportValues.ToText(new TimeSpan(12, 30, 0)));
        Assert.Equal("9007199254740993", ExportValues.ToText(9007199254740993L));
    }

    [Fact]
    public void Binario_vai_completo_no_CSV_sem_o_corte_da_grade()
    {
        var hex = ExportValues.ToText(new byte[1000]);
        Assert.Equal(2 + 2000, hex.Length);
    }

    [Fact]
    public void Celula_de_XLSX_preserva_numeros_datas_e_bit()
    {
        var d = new DateTime(2026, 9, 30);
        Assert.Equal(42, ExportValues.ToCell(42));
        Assert.Equal(1.5m, ExportValues.ToCell(1.5m));
        Assert.Equal(d, ExportValues.ToCell(d));
        Assert.Equal(true, ExportValues.ToCell(true));
        Assert.Null(ExportValues.ToCell(DBNull.Value));
        Assert.Equal("0x0A", ExportValues.ToCell(new byte[] { 10 }));
        Assert.Equal(d, ExportValues.ToCell(DateOnly.FromDateTime(d)));
    }

    [Fact]
    public void Volta_ao_tipo_original_valores_que_passaram_pelo_JSON()
    {
        Assert.Equal(1234.50m, ExportValues.Coerce("1234.50", new ColumnInfo("v", "number", "decimal")));
        Assert.Equal(9007199254740993L, ExportValues.Coerce("9007199254740993", new ColumnInfo("v", "number", "bigint")));
        Assert.Equal(new DateTime(2026, 9, 30, 13, 45, 10), ExportValues.Coerce("2026-09-30 13:45:10", new ColumnInfo("d", "date", "datetime2")));
        Assert.Equal(new DateTime(2026, 9, 30), ExportValues.Coerce("2026-09-30", new ColumnInfo("d", "date", "date")));
        Assert.IsType<DateTimeOffset>(ExportValues.Coerce("2026-09-30 13:45:10 -03:00", new ColumnInfo("d", "date", "datetimeoffset")));
        Assert.Equal("abc", ExportValues.Coerce("abc", new ColumnInfo("t", "text", "nvarchar")));
        Assert.Equal("não-número", ExportValues.Coerce("não-número", new ColumnInfo("v", "number", "decimal")));
        Assert.Equal(7, ExportValues.Coerce(7, new ColumnInfo("v", "number", "int")));
    }

    // ---- CSV ----

    [Fact]
    public void CSV_tem_BOM_UTF8_separador_ponto_e_virgula_e_protege_aspas_e_quebras()
    {
        var ms = new MemoryStream();
        var bytes = Write(new CsvRowWriter(ms), ms, 3);

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("Id;Nome;Valor;Criado;Ativo;Obs\r\n", text);
        Assert.Contains("1;\"Nome; 1\";1235.5;2026-10-01T13:45:10;0;\"linha\ncom \"\"aspas\"\"\"\r\n", text);
        Assert.Contains("3;\"Nome; 3\";1237.5;2026-10-03T13:45:10;0;", text); // NULL: campo vazio
    }

    [Fact]
    public void CSV_com_virgula_como_separador()
    {
        var ms = new MemoryStream();
        var bytes = Write(new CsvRowWriter(ms, ','), ms, 1);
        var text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("Id,Nome,Valor,Criado,Ativo,Obs\r\n", text);
        Assert.Contains("1,Nome; 1,1235.5,", text);
    }

    [Fact]
    public void CSV_acentos_ficam_em_UTF8()
    {
        var ms = new MemoryStream();
        using (var w = new CsvRowWriter(ms))
        {
            w.Begin([new ColumnInfo("Descrição", "text", "nvarchar")]);
            w.Write(["ação ç ñ 日本"], default);
            w.Complete();
        }
        Assert.Contains("Descrição", Encoding.UTF8.GetString(ms.ToArray()));
        Assert.Contains("ação ç ñ 日本", Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public void CSV_cancelado_interrompe()
    {
        var ms = new MemoryStream();
        using var w = new CsvRowWriter(ms);
        w.Begin(Cols);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => w.Write(Row(1), cts.Token));
    }

    // ---- XLSX ----

    private static string Entry(byte[] xlsx, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx));
        using var r = new StreamReader(zip.GetEntry(name)!.Open());
        return r.ReadToEnd().Replace("<x:", "<").Replace("</x:", "</"); // o MiniExcel escreve com prefixo "x:"
    }

    [Fact]
    public void XLSX_e_um_arquivo_valido_com_cabecalho_e_tipos_preservados()
    {
        var ms = new MemoryStream();
        var bytes = Write(new XlsxRowWriter(ms), ms, 5);

        var rows = new MemoryStream(bytes).Query(useHeaderRow: true).Cast<IDictionary<string, object>>().ToList();
        Assert.Equal(5, rows.Count);
        Assert.Equal(["Id", "Nome", "Valor", "Criado", "Ativo", "Obs"], rows[0].Keys);
        Assert.Equal("Nome; 1", rows[0]["Nome"]);
        Assert.IsNotType<string>(rows[0]["Id"]);               // número, não texto
        Assert.Equal(1235.5, Convert.ToDouble(rows[0]["Valor"]));
        Assert.IsType<DateTime>(rows[0]["Criado"]);            // data, não texto
        Assert.Equal(new DateTime(2026, 10, 1, 13, 45, 10), rows[0]["Criado"]);
        Assert.Null(rows[2]["Obs"]);                            // NULL = célula vazia
    }

    private static (bool Bold, string? NumFmt) StyleOf(byte[] xlsx, string cell)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var zip = new ZipArchive(new MemoryStream(xlsx));
        var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var s = int.Parse(sheet.Descendants(ns + "c").First(c => (string?)c.Attribute("r") == cell).Attribute("s")!.Value);
        var styles = XDocument.Load(zip.GetEntry("xl/styles.xml")!.Open());
        var xf = styles.Root!.Element(ns + "cellXfs")!.Elements(ns + "xf").ElementAt(s);
        var font = styles.Root.Element(ns + "fonts")!.Elements(ns + "font").ElementAt(int.Parse(xf.Attribute("fontId")!.Value));
        var fmtId = xf.Attribute("numFmtId")!.Value;
        var fmt = styles.Root.Element(ns + "numFmts")?.Elements(ns + "numFmt").FirstOrDefault(n => (string?)n.Attribute("numFmtId") == fmtId)?.Attribute("formatCode")?.Value;
        return (font.Element(ns + "b") is not null, fmt);
    }

    [Fact]
    public void XLSX_cabecalho_em_negrito_e_dados_normais()
    {
        var ms = new MemoryStream();
        var bytes = Write(new XlsxRowWriter(ms), ms, 3);

        foreach (var header in new[] { "A1", "B1", "F1" }) Assert.True(StyleOf(bytes, header).Bold, header);
        foreach (var data in new[] { "A2", "B2", "C2", "D2" }) Assert.False(StyleOf(bytes, data).Bold, data);
        // O arquivo continua legível depois do ajuste de estilo.
        Assert.Equal(3, new MemoryStream(bytes).Query(useHeaderRow: true).Count());
    }

    [Fact]
    public void XLSX_datetime_mostra_data_e_hora()
    {
        var ms = new MemoryStream();
        var bytes = Write(new XlsxRowWriter(ms), ms, 1);

        Assert.Equal("yyyy-mm-dd hh:mm:ss", StyleOf(bytes, "D2").NumFmt);
    }

    [Fact]
    public void XLSX_tipo_date_mostra_so_a_data()
    {
        var ms = new MemoryStream();
        using (var w = new XlsxRowWriter(ms))
        {
            w.Begin([new ColumnInfo("Dia", "date", "date")]);
            w.Write([new DateTime(2026, 9, 30)], default);
            w.Complete();
        }
        Assert.Equal("yyyy-mm-dd", StyleOf(ms.ToArray(), "A2").NumFmt);
    }

    [Fact]
    public void XLSX_sem_linhas_ainda_tem_o_cabecalho()
    {
        var ms = new MemoryStream();
        var bytes = Write(new XlsxRowWriter(ms), ms, 0);

        var rows = new MemoryStream(bytes).Query(useHeaderRow: false).Cast<IDictionary<string, object>>().ToList();
        Assert.Single(rows);
        Assert.Equal("Id", rows[0]["A"]);
        Assert.Equal("Obs", rows[0]["F"]);
    }

    [Fact]
    public void XLSX_nomes_de_coluna_repetidos_ou_vazios_ficam_unicos()
    {
        Assert.Equal(["Id", "Id_2", "coluna3", "ID_3"], XlsxRowWriter.UniqueHeaders(["Id", "Id", "", "ID"]));
    }

    [Fact]
    public void XLSX_streaming_de_100_mil_linhas_sem_estourar_memoria()
    {
        var path = Path.GetTempFileName();
        try
        {
            GC.Collect();
            var before = GC.GetTotalMemory(true);
            long peak = 0;
            using (var fs = File.Create(path))
            using (var w = new XlsxRowWriter(fs))
            {
                w.Begin(Cols);
                for (var i = 1; i <= 100_000; i++)
                {
                    w.Write(Row(i), default);
                    if (i % 10_000 == 0) peak = Math.Max(peak, GC.GetTotalMemory(false) - before);
                }
                w.Complete();
                Assert.Equal(100_000, w.Rows);
            }
            Assert.True(new FileInfo(path).Length > 1_000_000);
            // As 100 mil linhas ocupariam bem mais que isto se fossem acumuladas em memória.
            Assert.True(peak < 150L * 1024 * 1024, $"pico de memória alto: {peak / 1024 / 1024} MB");
            using var rows = File.OpenRead(path);
            Assert.Equal(100_000, rows.Query(useHeaderRow: true).Count());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CSV_streaming_de_300_mil_linhas()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var fs = File.Create(path))
            using (var w = new CsvRowWriter(fs))
            {
                w.Begin(Cols);
                for (var i = 1; i <= 300_000; i++) w.Write(Row(i), default);
                w.Complete();
            }
            using var reader = new CsvHelper.CsvReader(new StreamReader(path), new CsvHelper.Configuration.CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture) { Delimiter = ";" });
            var records = 0;
            while (reader.Read()) records++;
            Assert.Equal(300_001, records); // cabeçalho + linhas, mesmo com quebras de linha dentro dos campos
        }
        finally { File.Delete(path); }
    }
}
