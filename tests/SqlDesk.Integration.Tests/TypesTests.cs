using System.IO.Compression;
using SqlDesk.Core.Execution;
using SqlDesk.Core.Export;

namespace SqlDesk.Integration.Tests;

/// <summary>Tipos do MySQL e do MariaDB na grade (valores convertidos) e na exportação (valores brutos), com o driver de verdade.</summary>
public class TypesTests
{
    private static string Table(string prefix, string server) => $"{prefix}_{server}_{Guid.NewGuid().ToString("N")[..8]}";

    private static int Col(IReadOnlyList<ColumnInfo> cols, string name) => cols.ToList().FindIndex(c => c.Name == name);

    /// <summary>Lê o XLSX gerado e confere o tipo de cada célula: números e datas não podem virar texto.</summary>
    private static void AssertWorkbookTypes(string path, bool rerun)
    {
        var rows = MiniExcelLibs.MiniExcel.Query(path, useHeaderRow: true).Cast<IDictionary<string, object?>>().ToList();
        Assert.Equal(3, rows.Count);
        var r = rows[0];
        foreach (var numeric in new[] { "id", "big", "uint_", "bits", "dec30" })
            Assert.True(r[numeric] is not string, $"{numeric} deveria ser célula numérica, veio {r[numeric]?.GetType().Name} (rerun={rerun})");
        Assert.True(r["b1"] is not string, $"b1 deveria ser número/bool, veio {r["b1"]?.GetType().Name}");
        // O Excel guarda número como double (15 dígitos): o que importa é ser célula numérica e aproximadamente o valor.
        Assert.InRange(Convert.ToDouble(r["big"], System.Globalization.CultureInfo.InvariantCulture), 1.8446744073709e19, 1.8446744073710e19);
        Assert.Equal(4000000000m, Convert.ToDecimal(r["uint_"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsType<DateTime>(r["dt3"]);
        Assert.IsType<DateTime>(r["d"]);
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Tipos_na_grade_e_na_exportacao(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var t = Table("ty", server);
        var csv = Path.Combine(Path.GetTempPath(), $"sqldesk-{Guid.NewGuid():N}.csv");
        var xlsx = Path.Combine(Path.GetTempPath(), $"sqldesk-{Guid.NewGuid():N}.xlsx");
        try
        {
            await h.RunAsync(
                $"CREATE TABLE {t} (id INT PRIMARY KEY, b1 TINYINT(1), bits BIT(3), js JSON, bl BLOB, dec30 DECIMAL(30,10), " +
                "dt3 DATETIME(3), t3 TIME(3), big BIGINT UNSIGNED, uint_ INT UNSIGNED, d DATE) ENGINE=InnoDB; " +
                $"INSERT INTO {t} VALUES (1, 1, b'101', '{{\"a\": 1}}', 0xDEADBEEF, 12345678901234567890.1234567890, " +
                "'2024-03-05 10:20:30.123', '838:59:59', 18446744073709551615, 4000000000, '2024-03-05'); " +
                $"INSERT INTO {t} (id) VALUES (2); " +
                $"INSERT INTO {t} (id, t3) VALUES (3, '-01:30:00');");

            var sql = $"SELECT * FROM {t} ORDER BY id";

            // Grade: valores convertidos pelo CellValues.
            var (gs, grid, cap) = await h.RunCapturingAsync(sql, raw: false);
            Assert.Equal(RunStatus.Completed, gs.Status);
            Assert.DoesNotContain(grid.Messages, m => m.Kind == MessageKinds.Error);
            Assert.Equal(3, cap.Sets.Single().RowCount);
            var cols = grid.Columns.Single();
            var r1 = grid.AllRows[0];
            Assert.Equal(true, r1[Col(cols, "b1")]);                                    // TINYINT(1) -> bool
            Assert.Equal("12345678901234567890.123456789", r1[Col(cols, "dec30")]);     // DECIMAL -> texto (o driver descarta o zero final da escala)
            Assert.Equal("0xDEADBEEF", r1[Col(cols, "bl")]);                             // BLOB -> hex
            Assert.Contains("\"a\"", (string)r1[Col(cols, "js")]!);                      // JSON -> texto
            Assert.Equal("18446744073709551615", r1[Col(cols, "big")]);                  // BIGINT UNSIGNED -> texto
            Assert.Equal(4000000000u, r1[Col(cols, "uint_")]);                           // INT UNSIGNED -> número
            Assert.Equal("838:59:59", r1[Col(cols, "t3")]);                              // TIME além de 24 h
            Assert.Equal("2024-03-05 10:20:30.123", r1[Col(cols, "dt3")]);
            Assert.Equal("2024-03-05", r1[Col(cols, "d")]);
            Assert.Equal("5", r1[Col(cols, "bits")]);                                       // BIT(3): documentado no relatório
            Assert.Equal("-01:30:00", grid.AllRows[2][Col(cols, "t3")]);
            Assert.All(grid.AllRows[1].Skip(1), v => Assert.Null(v));                    // linha de NULLs

            // Exportação (caminho bruto): nenhum valor derruba o leitor.
            var (rs, raw, _) = await h.RunCapturingAsync(sql, raw: true);
            Assert.Equal(RunStatus.Completed, rs.Status);
            Assert.Equal(3, raw.AllRows.Count);

            var analyzer = h.Sessions.GetProvider(h.TabId)!.Analyzer;
            var c = await ExportService.ExportByRerunAsync(h.Runner, h.TabId, sql, analyzer, ExportFormat.Csv, csv, ';', 0, null, null, default);
            Assert.Equal(3, c.Rows);
            var lines = File.ReadAllText(csv).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(4, lines.Length); // cabeçalho + 3
            Assert.Contains("18446744073709551615", lines[1]);
            Assert.Contains("0xDEADBEEF", lines[1]);
            Assert.Contains("838:59:59", lines[1]);
            Assert.Contains("-01:30:00", lines[3]);

            var x = await ExportService.ExportByRerunAsync(h.Runner, h.TabId, sql, analyzer, ExportFormat.Xlsx, xlsx, ';', 0, null, null, default);
            Assert.Equal(3, x.Rows);
            using (var zip = ZipFile.OpenRead(xlsx)) Assert.Contains(zip.Entries, e => e.FullName.StartsWith("xl/worksheets/"));
            AssertWorkbookTypes(xlsx, rerun: true);

            // Também pelas linhas já carregadas (JSON da grade passando por Coerce).
            File.Delete(xlsx);
            var l = await ExportService.ExportLoadedAsync(ExportFormat.Xlsx, xlsx, ';', cols, grid.AllRows, null, default);
            Assert.Equal(3, l.Rows);
            Assert.True(new FileInfo(xlsx).Length > 0);
            AssertWorkbookTypes(xlsx, rerun: false);
        }
        finally
        {
            await h.RunAsync($"DROP TABLE IF EXISTS {t}");
            File.Delete(csv);
            File.Delete(xlsx);
        }
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Data_zero_nao_derruba_a_grade_nem_a_exportacao(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var t = Table("tz", server);
        var csv = Path.Combine(Path.GetTempPath(), $"sqldesk-{Guid.NewGuid():N}.csv");
        var xlsx = Path.Combine(Path.GetTempPath(), $"sqldesk-{Guid.NewGuid():N}.xlsx");
        try
        {
            await h.RunAsync($"CREATE TABLE {t} (id INT PRIMARY KEY, d DATE, dt DATETIME) ENGINE=InnoDB");
            try
            {
                await h.RunAsync($"SET SESSION sql_mode=''; INSERT INTO {t} VALUES (1, '0000-00-00', '0000-00-00 00:00:00')");
            }
            catch (InvalidOperationException)
            {
                Assert.Fail("zero-date-insert-recusado"); return;
            }
            var sql = $"SELECT * FROM {t}";

            var (gs, grid, _) = await h.RunCapturingAsync(sql, raw: false);
            Assert.Equal(RunStatus.Completed, gs.Status);
            Assert.Equal("0000-00-00", grid.AllRows.Single()[1]);
            Assert.Equal("0000-00-00 00:00:00", grid.AllRows.Single()[2]);

            var (rs, raw, _) = await h.RunCapturingAsync(sql, raw: true);
            Assert.Equal(RunStatus.Completed, rs.Status);
            Assert.Single(raw.AllRows);

            var analyzer = h.Sessions.GetProvider(h.TabId)!.Analyzer;
            var c = await ExportService.ExportByRerunAsync(h.Runner, h.TabId, sql, analyzer, ExportFormat.Csv, csv, ';', 0, null, null, default);
            Assert.Equal(1, c.Rows);
            Assert.Contains("0000-00-00", File.ReadAllText(csv));
            var x = await ExportService.ExportByRerunAsync(h.Runner, h.TabId, sql, analyzer, ExportFormat.Xlsx, xlsx, ';', 0, null, null, default);
            Assert.Equal(1, x.Rows);
            var zrow = MiniExcelLibs.MiniExcel.Query(xlsx, useHeaderRow: true).Cast<IDictionary<string, object?>>().Single();
            Assert.Equal("0000-00-00", zrow["d"]);
            Assert.Equal("0000-00-00T00:00:00", zrow["dt"]); // texto ISO, como no CSV
        }
        finally
        {
            await h.RunAsync($"DROP TABLE IF EXISTS {t}");
            File.Delete(csv);
            File.Delete(xlsx);
        }
    }
}
