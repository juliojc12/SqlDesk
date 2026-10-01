using System.Data;
using SqlDesk.Core.Connections;
using SqlDesk.Core.Execution;
using SqlDesk.Core.Sessions;
using SqlDesk.SqlAnalysis;

namespace SqlDesk.Core.Tests;

public class ExecutionTests
{
    private sealed class RecordingSink : IExecutionSink
    {
        public List<(int Index, DocRange Source, IReadOnlyList<ColumnInfo> Columns)> Started { get; } = [];
        public List<(int Index, int Count)> RowBatches { get; } = [];
        public List<(int Index, long Count, bool Truncated)> Completed { get; } = [];
        public List<object?[]> AllRows { get; } = [];

        public void ResultStarted(int resultIndex, DocRange source, IReadOnlyList<ColumnInfo> columns) => Started.Add((resultIndex, source, columns));

        public void Rows(int resultIndex, IReadOnlyList<object?[]> rows)
        {
            RowBatches.Add((resultIndex, rows.Count));
            AllRows.AddRange(rows);
        }

        public void ResultCompleted(int resultIndex, long rowCount, bool truncated) => Completed.Add((resultIndex, rowCount, truncated));

        public void Message(string kind, string text, int? line) { }
    }

    private static DataTable Numbers(int rows)
    {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(int));
        t.Columns.Add("Nome", typeof(string));
        for (var i = 1; i <= rows; i++) t.Rows.Add(i, $"n{i}");
        return t;
    }

    private static readonly DocRange Src = new(0, 10);

    [Fact]
    public async Task Envia_em_lotes_de_500_linhas()
    {
        var sink = new RecordingSink();
        using var reader = Numbers(1200).CreateDataReader();

        var (emitted, count, truncated) = await ResultStreamer.StreamCurrentAsync(reader, 0, Src, sink, null, default);

        Assert.True(emitted);
        Assert.Equal(1200, count);
        Assert.False(truncated);
        Assert.Equal([500, 500, 200], sink.RowBatches.Select(b => b.Count));
        Assert.Equal(1200, sink.AllRows.Count);
    }

    [Fact]
    public async Task Respeita_o_limite_de_linhas_e_marca_como_truncado()
    {
        var sink = new RecordingSink();
        using var reader = Numbers(1200).CreateDataReader();

        var (_, count, truncated) = await ResultStreamer.StreamCurrentAsync(reader, 0, Src, sink, 700, default);

        Assert.Equal(700, count);
        Assert.True(truncated);
        Assert.Equal(700, sink.AllRows.Count);
        Assert.Equal((0, 700L, true), sink.Completed.Single());
    }

    [Fact]
    public async Task Exatamente_no_limite_nao_e_truncado()
    {
        var sink = new RecordingSink();
        using var reader = Numbers(700).CreateDataReader();

        var (_, count, truncated) = await ResultStreamer.StreamCurrentAsync(reader, 0, Src, sink, 700, default);

        Assert.Equal(700, count);
        Assert.False(truncated);
    }

    [Fact]
    public async Task Result_set_sem_colunas_nao_gera_resultado()
    {
        var sink = new RecordingSink();
        using var reader = new DataTable().CreateDataReader();

        var (emitted, _, _) = await ResultStreamer.StreamCurrentAsync(reader, 0, Src, sink, null, default);

        Assert.False(emitted);
        Assert.Empty(sink.Started);
    }

    [Fact]
    public async Task Cancelamento_interrompe_a_leitura()
    {
        var sink = new RecordingSink();
        using var reader = Numbers(10).CreateDataReader();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ResultStreamer.StreamCurrentAsync(reader, 0, Src, sink, null, cts.Token));
    }

    [Fact]
    public async Task Descreve_as_colunas_com_categoria_para_ordenacao()
    {
        var t = new DataTable();
        t.Columns.Add("a", typeof(int));
        t.Columns.Add("b", typeof(string));
        t.Columns.Add("c", typeof(DateTime));
        t.Columns.Add("d", typeof(bool));
        t.Columns.Add("e", typeof(byte[]));
        var sink = new RecordingSink();
        using var reader = t.CreateDataReader();

        await ResultStreamer.StreamCurrentAsync(reader, 3, Src, sink, null, default);

        var (index, source, columns) = sink.Started.Single();
        Assert.Equal(3, index);
        Assert.Equal(Src, source);
        Assert.Equal(["number", "text", "date", "bool", "binary"], columns.Select(c => c.Kind));
        Assert.Equal(["a", "b", "c", "d", "e"], columns.Select(c => c.Name));
    }

    [Fact]
    public void Converte_valores_sem_perder_precisao()
    {
        Assert.Null(CellValues.Convert(null));
        Assert.Null(CellValues.Convert(DBNull.Value));
        Assert.Equal(42, CellValues.Convert(42));
        Assert.Equal("9007199254740993", CellValues.Convert(9007199254740993L));
        Assert.Equal("1234.5600", CellValues.Convert(1234.5600m));
        Assert.Equal("2026-09-30 13:45:10", CellValues.Convert(new DateTime(2026, 9, 30, 13, 45, 10)));
        Assert.Equal("2026-09-30 13:45:10.25", CellValues.Convert(new DateTime(2026, 9, 30, 13, 45, 10, 250)));
        Assert.Equal("2026-09-30", CellValues.Convert(new DateTime(2026, 9, 30), "date"));
        Assert.Equal("0x0AFF", CellValues.Convert(new byte[] { 0x0A, 0xFF }));
        Assert.Equal("12:30:00", CellValues.Convert(new TimeSpan(12, 30, 0)));
        Assert.Equal("d1b3c4a0-0000-0000-0000-000000000001", CellValues.Convert(Guid.Parse("d1b3c4a0-0000-0000-0000-000000000001")));
    }

    [Fact]
    public void Corta_texto_e_binario_muito_grandes()
    {
        var text = (string)CellValues.Convert(new string('x', CellValues.MaxTextLength + 10))!;
        Assert.Equal(CellValues.MaxTextLength + 1, text.Length);
        Assert.EndsWith("…", text);

        var hex = (string)CellValues.Convert(new byte[1000])!;
        Assert.Equal(2 + 64 * 2 + 1, hex.Length);
    }

    [Fact]
    public async Task Executar_sem_sessao_conectada_falha_com_mensagem_clara()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new ConnectionStore(Path.Combine(dir, "c.json"), new FakeProtector());
            var runner = new QueryRunner(new TabSessionManager(store));

            await Assert.ThrowsAsync<TabNotConnectedException>(
                () => runner.RunAsync("aba", BatchSplitter.Split("SELECT 1"), 0, 1, null, new RecordingSink()));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Cancelar_aba_sem_execucao_nao_faz_nada()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var runner = new QueryRunner(new TabSessionManager(new ConnectionStore(Path.Combine(dir, "c.json"), new FakeProtector())));
            runner.Cancel("inexistente");
            Assert.False(runner.IsRunning("inexistente"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(12, "12 ms")]
    [InlineData(1500, "1.50 s")]
    [InlineData(65_000, "1 min 5 s")]
    public void Formata_o_tempo(int ms, string esperado) =>
        Assert.Equal(esperado, QueryRunner.FormatElapsed(TimeSpan.FromMilliseconds(ms)).Replace(',', '.'));

    private sealed class FakeProtector : IPasswordProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
