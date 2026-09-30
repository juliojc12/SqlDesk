namespace SqlDesk.SqlAnalysis.Tests;

public class StatementLocatorTests
{
    /// <summary>O texto traz um '|' marcando o cursor. Devolve o statement escolhido (ou null) e o resultado.</summary>
    private static (string? Picked, LocateResult Result) Locate(string withCursor)
    {
        var cursor = withCursor.IndexOf('|');
        var text = withCursor.Remove(cursor, 1);
        var r = StatementLocator.Locate(text, cursor);
        return (r.Range is { } rg ? text.Substring(rg.Start, rg.Length) : null, r);
    }

    [Fact]
    public void Cursor_dentro_do_statement()
    {
        Assert.Equal("SELECT 2 FROM t", Locate("SELECT 1;\nSELECT 2 FR|OM t;\nSELECT 3;").Picked?.TrimEnd(';'));
    }

    [Fact]
    public void Statement_de_varias_linhas_com_cursor_em_qualquer_linha()
    {
        var sql = "SELECT a,\n       b\nFROM t\nWHERE x = 1";
        Assert.Equal(sql, Locate("SELECT a,\n       |b\nFROM t\nWHERE x = 1").Picked);
        Assert.Equal(sql, Locate("SELECT a,\n       b\nFROM t\nWHERE x =| 1").Picked);
        Assert.Equal(sql, Locate("|SELECT a,\n       b\nFROM t\nWHERE x = 1").Picked);
    }

    [Fact]
    public void Cursor_no_fim_do_statement()
    {
        Assert.Equal("SELECT 1;", Locate("SELECT 1;|\nSELECT 2;").Picked);
        Assert.Equal("SELECT 1", Locate("SELECT 1|\nSELECT 2").Picked);
    }

    [Fact]
    public void Cursor_depois_do_fim_na_mesma_linha_usa_esse_statement()
    {
        Assert.Equal("SELECT 1;", Locate("SELECT 1;    |\nSELECT 2;").Picked);
        Assert.Equal("SELECT 1", Locate("SELECT 1 -- nota |\nSELECT 2").Picked);
    }

    [Fact]
    public void Cursor_em_linha_em_branco_nao_executa_nada_e_avisa()
    {
        var (picked, r) = Locate("SELECT 1;\n|\nSELECT 2;");
        Assert.Null(picked);
        Assert.Contains("em branco", r.Message);
    }

    [Fact]
    public void Cursor_em_linha_so_de_espacos_tambem_e_linha_em_branco()
    {
        var (picked, r) = Locate("SELECT 1;\n   |   \nSELECT 2;");
        Assert.Null(picked);
        Assert.Contains("em branco", r.Message);
    }

    [Fact]
    public void Dois_statements_na_mesma_linha()
    {
        Assert.Equal("SELECT 2", Locate("SELECT 1; SELECT |2").Picked);
        Assert.Equal("SELECT 1;", Locate("SEL|ECT 1; SELECT 2").Picked);
    }

    [Fact]
    public void Cursor_colado_entre_dois_statements_prefere_o_que_termina_ali() =>
        Assert.Equal("SELECT 1;", Locate("SELECT 1;|SELECT 2").Picked);

    [Fact]
    public void Cursor_na_indentacao_antes_do_statement()
    {
        Assert.Equal("SELECT 2", Locate("SELECT 1;\n|   SELECT 2").Picked);
        Assert.Equal("SELECT 2", Locate("SELECT 1;\n  | SELECT 2").Picked);
    }

    [Fact]
    public void Statement_composto_e_escolhido_por_inteiro()
    {
        var sql = "IF 1 = 1\nBEGIN\n    UPDATE t SET a = 1 WHERE id = 2;\n    PRINT 'x';\nEND";
        Assert.Equal(sql, Locate("IF 1 = 1\nBEGIN\n    UPDATE t SET a = 1 WH|ERE id = 2;\n    PRINT 'x';\nEND").Picked);
    }

    [Fact]
    public void Comentario_colado_em_cima_do_statement_seleciona_o_statement()
    {
        Assert.Equal("SELECT 2", Locate("SELECT 1;\n\n-- busca |pedidos\nSELECT 2").Picked);
    }

    [Fact]
    public void Comentario_separado_por_linha_em_branco_nao_seleciona_nada()
    {
        var (picked, _) = Locate("SELECT 1;\n\n-- so um |comentario\n\nSELECT 2");
        Assert.Null(picked);
    }

    [Fact]
    public void Respeita_go_como_separador_de_batch()
    {
        Assert.Equal("SELECT 2", Locate("SELECT 1\nGO\nSELECT |2\nGO\nSELECT 3").Picked);
        var (picked, r) = Locate("SELECT 1\nG|O\nSELECT 2");
        Assert.Null(picked);
        Assert.Contains("GO", r.Message);
    }

    [Fact]
    public void Go_dentro_de_string_nao_quebra_o_statement()
    {
        var sql = "SELECT 'a\nGO\nb'";
        Assert.Equal(sql, Locate("SELECT 'a\nG|O\nb'").Picked);
    }

    [Fact]
    public void Erro_de_sintaxe_em_outro_batch_nao_atrapalha_o_batch_valido()
    {
        var (picked, r) = Locate("SELECT FROM WHERE\nGO\nSELECT 1; SELECT |2");
        Assert.Equal("SELECT 2", picked);
        Assert.False(r.UsedFallback);
    }

    // ---------- fallback ----------

    [Fact]
    public void Fallback_usa_o_bloco_entre_linhas_em_branco()
    {
        var (picked, r) = Locate("SELECT FROM\n\nSELECT 2 FR|OM\nWHERE\n\nSELECT 3");
        Assert.True(r.UsedFallback);
        Assert.Equal("SELECT 2 FROM\nWHERE", picked);
    }

    [Fact]
    public void Fallback_usa_ponto_e_virgula_como_delimitador()
    {
        var (picked, r) = Locate("SELECT FROM; SELECT 2 FR|OM; SELECT 3");
        Assert.True(r.UsedFallback);
        Assert.Equal("SELECT 2 FROM;", picked);
    }

    [Fact]
    public void Fallback_ignora_ponto_e_virgula_dentro_de_string_e_comentario()
    {
        var (picked, _) = Locate("SELECT FROM 'a;b' /* ; */ WH|ERE -- ;\nx");
        Assert.Equal("SELECT FROM 'a;b' /* ; */ WHERE -- ;\nx", picked);
    }

    [Fact]
    public void Fallback_cursor_logo_depois_do_ponto_e_virgula_pertence_ao_anterior()
    {
        var (picked, _) = Locate("SELECT FROM 1;| SELECT 2");
        Assert.Equal("SELECT FROM 1;", picked);
    }

    [Fact]
    public void Fallback_em_linha_em_branco_nao_executa_nada()
    {
        var (picked, r) = Locate("SELECT FROM\n|\nSELECT 2");
        Assert.Null(picked);
        Assert.True(r.UsedFallback);
    }

    [Fact]
    public void Fallback_fica_dentro_do_batch_do_cursor()
    {
        var (picked, _) = Locate("SELECT 1\nGO\nSELECT FROM\nx|\nGO\nSELECT 3");
        Assert.Equal("SELECT FROM\nx", picked);
    }

    // ---------- bordas ----------

    [Fact]
    public void Texto_vazio_nao_encontra_nada()
    {
        var r = StatementLocator.Locate("", 0);
        Assert.False(r.Found);
        Assert.NotNull(r.Message);
    }

    [Fact]
    public void Cursor_fora_do_texto_e_limitado()
    {
        Assert.True(StatementLocator.Locate("SELECT 1", 999).Found);
        Assert.True(StatementLocator.Locate("SELECT 1", -5).Found);
    }

    [Fact]
    public void Crlf_nao_desloca_os_offsets()
    {
        var sql = "SELECT 1;\r\nSELECT 2;\r\nSELECT 3;";
        var r = StatementLocator.Locate(sql, sql.IndexOf("SELECT 2", StringComparison.Ordinal) + 3);
        Assert.Equal("SELECT 2;", sql.Substring(r.Range!.Start, r.Range.Length));
    }

    [Fact]
    public void Acentos_e_caracteres_fora_do_bmp_nao_quebram_os_offsets()
    {
        var sql = "SELECT 'ação 😀' AS a;\nSELECT 'ç' AS b;";
        var r = StatementLocator.Locate(sql, sql.IndexOf("AS b", StringComparison.Ordinal));
        Assert.Equal("SELECT 'ç' AS b;", sql.Substring(r.Range!.Start, r.Range.Length));
    }
}
