using System.Text;
using MiniExcelLibs;
using SqlDesk.Core.Execution;
using SqlDesk.Core.Export;
using SqlDesk.SqlAnalysis;

namespace SqlDesk.Core.Tests;

public class ExportServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    private string P(string name) => Path.Combine(_dir, name);

    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly ColumnInfo[] Cols = [new("Id", "number", "int"), new("Valor", "number", "decimal"), new("Criado", "date", "datetime2")];

    /// <summary>Roteiro de um leitor: emite result sets com valores brutos, como o SqlDataReader.</summary>
    private sealed class FakeRunner(Action<IExecutionSink> script, string status = RunStatus.Completed) : IBatchRunner
    {
        public IReadOnlyList<Batch>? Batches { get; private set; }
        public bool IsRunning(string tabId) => false;

        public Task<RunSummary> RunAsync(string tabId, IReadOnlyList<Batch> batches, int baseOffset, int baseLine, int? maxRows,
            IExecutionSink sink, CancellationToken externalCt = default)
        {
            Batches = batches;
            Assert.Null(maxRows); // a exportação não tem o limite de linhas da grade
            Assert.True(sink.WantsRawValues);
            script(sink);
            return Task.FromResult(new RunSummary(status, 5, 0));
        }
    }

    private static void EmitSet(IExecutionSink s, int index, int rows, ColumnInfo[]? cols = null)
    {
        s.ResultStarted(index, new DocRange(0, 1), cols ?? Cols);
        s.Rows(index, Enumerable.Range(1, rows).Select(i => new object?[] { i, 10.5m * i, new DateTime(2026, 1, 1).AddDays(i) }).ToList());
        s.ResultCompleted(index, rows, false);
    }

    // ---- linhas carregadas ----

    [Fact]
    public async Task Exporta_as_linhas_carregadas_em_CSV_na_ordem_recebida_e_volta_aos_tipos()
    {
        var rows = new List<object?[]> { new object?[] { 2, "20.50", "2026-02-02 00:00:00" }, new object?[] { 1, "10.5", "2026-01-01 00:00:00" } };

        var r = await ExportService.ExportLoadedAsync(ExportFormat.Csv, P("a.csv"), ';', Cols, rows, null, default);

        Assert.Equal(2, r.Rows);
        var text = File.ReadAllText(P("a.csv"), Encoding.UTF8);
        Assert.Equal("Id;Valor;Criado\r\n2;20.50;2026-02-02T00:00:00\r\n1;10.5;2026-01-01T00:00:00\r\n", text.TrimStart('﻿'));
    }

    [Fact]
    public async Task Exporta_as_linhas_carregadas_em_XLSX_preservando_tipos()
    {
        var rows = new List<object?[]> { new object?[] { 1, "10.5", "2026-01-01 08:30:00" } };

        await ExportService.ExportLoadedAsync(ExportFormat.Xlsx, P("a.xlsx"), ';', Cols, rows, null, default);

        var row = MiniExcel.Query(P("a.xlsx"), useHeaderRow: true).Cast<IDictionary<string, object>>().Single();
        Assert.IsType<DateTime>(row["Criado"]);
        Assert.Equal(new DateTime(2026, 1, 1, 8, 30, 0), row["Criado"]);
        Assert.Equal(10.5, Convert.ToDouble(row["Valor"]));
    }

    [Fact]
    public async Task Nao_deixa_arquivo_temporario_nem_arquivo_pela_metade_ao_cancelar()
    {
        using var cts = new CancellationTokenSource();
        IEnumerable<object?[]> Rows()
        {
            for (var i = 0; i < 100; i++)
            {
                if (i == 10) cts.Cancel();
                yield return [i, "1", "2026-01-01"];
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ExportService.ExportLoadedAsync(ExportFormat.Csv, P("c.csv"), ';', Cols, Rows(), null, cts.Token));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Falha_nao_apaga_um_arquivo_anterior_de_mesmo_nome()
    {
        File.WriteAllText(P("keep.csv"), "antigo");
        IEnumerable<object?[]> Boom() { yield return [1, "1", "2026-01-01"]; throw new InvalidOperationException("falhou"); }

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExportService.ExportLoadedAsync(ExportFormat.Csv, P("keep.csv"), ';', Cols, Boom(), null, default));

        Assert.Equal("antigo", File.ReadAllText(P("keep.csv")));
        Assert.Equal([P("keep.csv")], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Sobrescreve_o_arquivo_anterior_no_sucesso()
    {
        File.WriteAllText(P("o.csv"), "antigo");
        await ExportService.ExportLoadedAsync(ExportFormat.Csv, P("o.csv"), ',', Cols, [new object?[] { 1, "1", "2026-01-01" }], null, default);
        Assert.StartsWith("Id,Valor,Criado", File.ReadAllText(P("o.csv")).TrimStart('﻿'));
    }

    [Fact]
    public async Task Informa_o_progresso_a_cada_5_mil_linhas()
    {
        var seen = new List<long>();
        var rows = Enumerable.Range(0, 12_000).Select(i => new object?[] { i, "1", "2026-01-01" });

        await ExportService.ExportLoadedAsync(ExportFormat.Csv, P("p.csv"), ';', Cols, rows, seen.Add, default);

        Assert.Equal([5_000L, 10_000L], seen);
    }

    // ---- reexecução ----

    [Fact]
    public async Task Reexecuta_e_grava_direto_no_arquivo_com_valores_brutos_sem_limite()
    {
        var runner = new FakeRunner(s => EmitSet(s, 0, 25_000));

        var r = await ExportService.ExportByRerunAsync(runner, "t", "SELECT * FROM dbo.Grande", SqlServerAnalyzer.Instance, ExportFormat.Csv, P("g.csv"), ';', 0, null, null, default);

        Assert.Equal(25_000, r.Rows);
        Assert.Equal(25_001, File.ReadLines(P("g.csv")).Count());
        Assert.Equal("1;10.5;2026-01-02T00:00:00", File.ReadLines(P("g.csv")).Skip(1).First());
    }

    [Fact]
    public async Task Reexecucao_respeita_a_ordem_de_colunas_escolhida()
    {
        var runner = new FakeRunner(s => EmitSet(s, 0, 2));

        await ExportService.ExportByRerunAsync(runner, "t", "SELECT 1", SqlServerAnalyzer.Instance, ExportFormat.Csv, P("o.csv"), ';', 0, [2, 0], null, default);

        var lines = File.ReadAllLines(P("o.csv"));
        Assert.Equal("Criado;Id", lines[0].TrimStart('﻿'));
        Assert.Equal("2026-01-02T00:00:00;1", lines[1]);
    }

    [Fact]
    public async Task Reexecucao_pega_o_result_set_pedido_quando_o_texto_produz_varios()
    {
        var runner = new FakeRunner(s =>
        {
            EmitSet(s, 0, 1);
            EmitSet(s, 1, 3, [new("Outro", "number", "int"), new("Valor", "number", "decimal"), new("Criado", "date", "datetime2")]);
        });

        var r = await ExportService.ExportByRerunAsync(runner, "t", "SELECT 1; SELECT 2", SqlServerAnalyzer.Instance, ExportFormat.Csv, P("m.csv"), ';', 1, null, null, default);

        Assert.Equal(3, r.Rows);
        Assert.StartsWith("Outro;", File.ReadAllText(P("m.csv")).TrimStart('﻿'));
    }

    [Fact]
    public async Task Reexecucao_em_XLSX_mantem_numeros_e_datas_brutos()
    {
        var runner = new FakeRunner(s => EmitSet(s, 0, 3));

        await ExportService.ExportByRerunAsync(runner, "t", "SELECT 1", SqlServerAnalyzer.Instance, ExportFormat.Xlsx, P("r.xlsx"), ';', 0, null, null, default);

        var rows = MiniExcel.Query(P("r.xlsx"), useHeaderRow: true).Cast<IDictionary<string, object>>().ToList();
        Assert.Equal(3, rows.Count);
        Assert.IsType<DateTime>(rows[0]["Criado"]);
    }

    [Theory]
    [InlineData("UPDATE dbo.T SET a = 1 WHERE id = 1")]
    [InlineData("INSERT INTO dbo.T (a) VALUES (1)")]
    [InlineData("EXEC dbo.pr_Faz")]
    [InlineData("SELECT * INTO dbo.copia FROM dbo.T")]
    [InlineData("SELECT 1; DELETE FROM dbo.T WHERE id = 1")]
    public async Task Nao_reexecuta_texto_que_escreve_e_nao_toca_no_banco_nem_no_arquivo(string sql)
    {
        var runner = new FakeRunner(_ => throw new InvalidOperationException("não deveria executar"));

        var ex = await Assert.ThrowsAsync<ExportFailedException>(
            () => ExportService.ExportByRerunAsync(runner, "t", sql, SqlServerAnalyzer.Instance, ExportFormat.Csv, P("x.csv"), ';', 0, null, null, default));

        Assert.Contains("não é seguro reexecutar", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(runner.Batches);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Theory]
    [InlineData("UPDATE dbo.T SET a = 1")]
    [InlineData("DELETE FROM dbo.T")]
    [InlineData("TRUNCATE TABLE dbo.T")]
    public async Task Nao_reexecuta_comando_destrutivo(string sql)
    {
        var runner = new FakeRunner(_ => throw new InvalidOperationException("não deveria executar"));

        var ex = await Assert.ThrowsAsync<ExportFailedException>(
            () => ExportService.ExportByRerunAsync(runner, "t", sql, SqlServerAnalyzer.Instance, ExportFormat.Csv, P("x.csv"), ';', 0, null, null, default));

        Assert.Contains("confirmação", ex.Message);
    }

    [Theory]
    [InlineData("DELETE FROM t")]
    [InlineData("DELETE FROM t WHERE id = 1")]
    [InlineData("SELECT * FROM t INTO OUTFILE '/tmp/x.txt'")]
    [InlineData("INSERT INTO t (a) VALUES (1)")]
    public async Task Reexecucao_respeita_a_recusa_do_analisador_do_mysql(string sql)
    {
        var runner = new FakeRunner(_ => throw new InvalidOperationException("não deveria executar"));

        await Assert.ThrowsAsync<ExportFailedException>(
            () => ExportService.ExportByRerunAsync(runner, "t", sql, MySqlAnalyzer.Instance, ExportFormat.Csv, P("m.csv"), ';', 0, null, null, default));

        Assert.Null(runner.Batches);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    /// <summary>Analisador que diz que nada é só leitura, para provar que a reexecução usa o analisador injetado.</summary>
    private sealed class NothingReadOnlyAnalyzer : ISqlAnalyzer
    {
        public ScriptAnalysis Analyze(string script) => MySqlAnalyzer.Instance.Analyze(script);
        public LocateResult Locate(string text, int cursor) => MySqlAnalyzer.Instance.Locate(text, cursor);
        public RewriteResult Rewrite(string script) => MySqlAnalyzer.Instance.Rewrite(script);
        public bool IsReadOnly(string script, out string? reason) { reason = "analisador de teste"; return false; }
    }

    [Fact]
    public async Task Reexecucao_usa_o_analisador_injetado_e_recusa_quando_ele_nao_ve_somente_leitura()
    {
        var runner = new FakeRunner(_ => throw new InvalidOperationException("não deveria executar"));

        var ex = await Assert.ThrowsAsync<ExportFailedException>(
            () => ExportService.ExportByRerunAsync(runner, "t", "SELECT 1", new NothingReadOnlyAnalyzer(), ExportFormat.Csv, P("n.csv"), ';', 0, null, null, default));

        Assert.Contains("analisador de teste", ex.Message);
        Assert.Null(runner.Batches);
    }

    [Fact]
    public async Task Erro_do_servidor_vira_mensagem_e_nao_deixa_arquivo()
    {
        var runner = new FakeRunner(s =>
        {
            EmitSet(s, 0, 5);
            s.Message(MessageKinds.Error, "Timeout expirado.", null);
        }, RunStatus.Error);

        var ex = await Assert.ThrowsAsync<ExportFailedException>(
            () => ExportService.ExportByRerunAsync(runner, "t", "SELECT 1", SqlServerAnalyzer.Instance, ExportFormat.Csv, P("e.csv"), ';', 0, null, null, default));

        Assert.Equal("Timeout expirado.", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Cancelamento_durante_a_reexecucao_nao_deixa_arquivo()
    {
        var runner = new FakeRunner(s => EmitSet(s, 0, 2), RunStatus.Cancelled);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ExportService.ExportByRerunAsync(runner, "t", "SELECT 1", SqlServerAnalyzer.Instance, ExportFormat.Csv, P("c.csv"), ';', 0, null, null, cts.Token));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Resultado_que_nao_aparece_na_reexecucao_e_erro_claro()
    {
        var runner = new FakeRunner(_ => { });

        var ex = await Assert.ThrowsAsync<ExportFailedException>(
            () => ExportService.ExportByRerunAsync(runner, "t", "SELECT 1", SqlServerAnalyzer.Instance, ExportFormat.Csv, P("n.csv"), ';', 0, null, null, default));

        Assert.Contains("não produziu o resultado", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }
}
