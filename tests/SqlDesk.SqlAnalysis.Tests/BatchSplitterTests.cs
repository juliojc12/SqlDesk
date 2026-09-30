namespace SqlDesk.SqlAnalysis.Tests;

public class BatchSplitterTests
{
    private static string[] Texts(string sql) => BatchSplitter.Split(sql).Select(b => b.Text.Trim()).ToArray();

    [Fact]
    public void Separa_por_go_sozinho_na_linha() =>
        Assert.Equal(["SELECT 1", "SELECT 2"], Texts("SELECT 1\nGO\nSELECT 2"));

    [Theory]
    [InlineData("go")]
    [InlineData("Go")]
    [InlineData("   GO")]
    [InlineData("\tGO  ")]
    [InlineData("GO -- fim do batch")]
    [InlineData("GO--colado")]
    [InlineData("GO /* comentario */")]
    [InlineData("GO /* a */ /* b */ -- c")]
    [InlineData("GO 1")]
    [InlineData("GO 3 -- tres vezes")]
    public void Variacoes_validas_de_go(string goLine) =>
        Assert.Equal(["SELECT 1", "SELECT 2"], Texts($"SELECT 1\n{goLine}\nSELECT 2"));

    [Theory]
    [InlineData("GOTO fim")]
    [InlineData("GO;")]
    [InlineData("GO SELECT 1")]
    [InlineData("GOAL")]
    [InlineData("GO_1")]
    [InlineData("SELECT 1 GO")]
    [InlineData("GO 0")]
    [InlineData("GO x")]
    [InlineData("GO /* sem fechar")]
    [InlineData("AGO")]
    public void Linhas_que_nao_sao_go_nao_separam(string line)
    {
        var batches = BatchSplitter.Split($"SELECT 1\n{line}\nSELECT 2");
        Assert.Single(batches);
    }

    [Fact]
    public void Go_dentro_de_string_de_varias_linhas_e_ignorado() =>
        Assert.Single(BatchSplitter.Split("SELECT 'linha 1\nGO\nlinha 3'"));

    [Fact]
    public void Go_dentro_de_comentario_de_bloco_e_ignorado() =>
        Assert.Single(BatchSplitter.Split("/* antes\nGO\ndepois */ SELECT 1"));

    [Fact]
    public void Go_dentro_de_comentario_de_bloco_aninhado_e_ignorado() =>
        Assert.Single(BatchSplitter.Split("/* a /* b\nGO\n */ c\nGO\n */ SELECT 1"));

    [Fact]
    public void Go_dentro_de_identificador_entre_colchetes_ou_aspas_e_ignorado()
    {
        Assert.Single(BatchSplitter.Split("SELECT [a\nGO\nb] FROM t"));
        Assert.Single(BatchSplitter.Split("SELECT \"a\nGO\nb\" FROM t"));
    }

    [Fact]
    public void Aspas_escapadas_nao_encerram_a_string()
    {
        Assert.Single(BatchSplitter.Split("SELECT 'it''s\nGO\nstill string'"));
        Assert.Equal(2, BatchSplitter.Split("SELECT 'it''s'\nGO\nSELECT 2").Count);
    }

    [Fact]
    public void Comentario_de_linha_nao_vaza_para_a_linha_seguinte() =>
        Assert.Equal(["-- GO aqui nao conta\nSELECT 1", "SELECT 2"], Texts("-- GO aqui nao conta\nSELECT 1\nGO\nSELECT 2"));

    [Fact]
    public void Go_no_inicio_no_fim_e_repetido_nao_gera_batches_vazios()
    {
        Assert.Equal(["SELECT 1"], Texts("GO\nSELECT 1\nGO"));
        Assert.Equal(["SELECT 1", "SELECT 2"], Texts("SELECT 1\nGO\nGO\n\nGO\nSELECT 2\nGO\nGO"));
        Assert.Empty(BatchSplitter.Split(""));
        Assert.Empty(BatchSplitter.Split("GO"));
        Assert.Empty(BatchSplitter.Split("  \n GO \n  "));
    }

    [Fact]
    public void Suporta_crlf_e_cr()
    {
        Assert.Equal(["SELECT 1", "SELECT 2"], Texts("SELECT 1\r\nGO\r\nSELECT 2"));
        Assert.Equal(["SELECT 1", "SELECT 2"], Texts("SELECT 1\rGO\rSELECT 2"));
    }

    [Fact]
    public void Go_na_ultima_linha_sem_quebra_final() =>
        Assert.Equal(["SELECT 1"], Texts("SELECT 1\nGO"));

    [Fact]
    public void Offsets_e_linhas_apontam_para_o_documento_original()
    {
        var sql = "SELECT 1\nGO\n\nSELECT 2\nFROM t\nGO\nSELECT 3";
        var b = BatchSplitter.Split(sql);
        Assert.Equal(3, b.Count);
        Assert.Equal([1, 3, 7], b.Select(x => x.StartLine)); // o batch 2 começa na linha em branco logo após o GO
        foreach (var batch in b) Assert.Equal(batch.Text, sql.Substring(batch.Start, batch.Text.Length));
        Assert.Equal("\nSELECT 2\nFROM t\n", b[1].Text.Replace("\n\n", "\n"));
    }

    [Fact]
    public void Contagem_do_go_fica_no_batch_anterior()
    {
        var b = BatchSplitter.Split("SELECT 1\nGO 5\nSELECT 2\nGO\nSELECT 3");
        Assert.Equal([5, 1, 1], b.Select(x => x.RepeatCount));
    }

    [Fact]
    public void Contagem_nao_e_atribuida_a_batch_vazio_anterior()
    {
        var b = BatchSplitter.Split("SELECT 1\nGO\nGO 9\nSELECT 2");
        Assert.Equal([1, 1], b.Select(x => x.RepeatCount));
    }

    [Fact]
    public void Indices_sao_sequenciais()
    {
        var b = BatchSplitter.Split("SELECT 1\nGO\nSELECT 2\nGO\nSELECT 3");
        Assert.Equal([0, 1, 2], b.Select(x => x.Index));
    }
}
