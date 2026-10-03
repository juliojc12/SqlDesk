using SqlDesk.Core.Export;

namespace SqlDesk.Integration.Tests;

/// <summary>Exportação por reexecução (streaming) de um SELECT grande, com o driver e o servidor de verdade.</summary>
public class ExportTests
{
    private const int Rows = 20_000;

    /// <summary>Cria uma tabela de apoio e a enche por duplicação (32 768 linhas; o AUTO_INCREMENT deixa buracos em INSERT ... SELECT, por isso o LIMIT), sem depender de CTE recursiva.</summary>
    private static async Task FillAsync(Harness h, string t)
    {
        var doubling = string.Concat(Enumerable.Repeat($"INSERT INTO {t} (v) SELECT v FROM {t}; ", 15));
        await h.RunAsync($"CREATE TABLE {t} (id INT AUTO_INCREMENT PRIMARY KEY, v INT NOT NULL) ENGINE=InnoDB; " +
                         $"INSERT INTO {t} (v) VALUES (7); {doubling}");
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Exporta_20_mil_linhas_por_reexecucao_em_csv_e_xlsx(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var t = $"exp_{server}_{Guid.NewGuid().ToString("N")[..8]}";
        var csv = Path.Combine(Path.GetTempPath(), $"sqldesk-{Guid.NewGuid():N}.csv");
        var xlsx = Path.Combine(Path.GetTempPath(), $"sqldesk-{Guid.NewGuid():N}.xlsx");
        try
        {
            await FillAsync(h, t);
            var sql = $"SELECT id, v FROM {t} ORDER BY id LIMIT {Rows}";
            var analyzer = h.Sessions.GetProvider(h.TabId)!.Analyzer;

            var rc = await ExportService.ExportByRerunAsync(h.Runner, h.TabId, sql, analyzer, ExportFormat.Csv, csv, ';', 0, null, null, default);
            Assert.Equal(Rows, rc.Rows);
            Assert.Equal(Rows + 1, File.ReadLines(csv).Count());   // cabeçalho + linhas

            var rx = await ExportService.ExportByRerunAsync(h.Runner, h.TabId, sql, analyzer, ExportFormat.Xlsx, xlsx, ';', 0, null, null, default);
            Assert.Equal(Rows, rx.Rows);
            Assert.Equal(Rows, MiniExcelLibs.MiniExcel.Query(xlsx, useHeaderRow: true).Count());

            // A aba continua usável depois da exportação.
            Assert.Equal(1, await h.ScalarAsync("SELECT 1"));
        }
        finally
        {
            await h.RunAsync($"DROP TABLE IF EXISTS {t}");
            File.Delete(csv);
            File.Delete(xlsx);
        }
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Reexecucao_recusa_escrita_e_nao_toca_no_banco(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var t = $"expw_{server}_{Guid.NewGuid().ToString("N")[..8]}";
        var csv = Path.Combine(Path.GetTempPath(), $"sqldesk-{Guid.NewGuid():N}.csv");
        try
        {
            await h.RunAsync($"CREATE TABLE {t} (id INT PRIMARY KEY) ENGINE=InnoDB; INSERT INTO {t} VALUES (1), (2)");
            var analyzer = h.Sessions.GetProvider(h.TabId)!.Analyzer;

            foreach (var sql in new[] { $"DELETE FROM {t}", $"INSERT INTO {t} VALUES (3)", $"SELECT * FROM {t} INTO OUTFILE '/tmp/{t}.txt'" })
                await Assert.ThrowsAsync<ExportFailedException>(
                    () => ExportService.ExportByRerunAsync(h.Runner, h.TabId, sql, analyzer, ExportFormat.Csv, csv, ';', 0, null, null, default));

            Assert.Equal(2, await h.ScalarAsync($"SELECT COUNT(*) FROM {t}"));
            Assert.False(File.Exists(csv));
        }
        finally
        {
            await h.RunAsync($"DROP TABLE IF EXISTS {t}");
        }
    }
}
