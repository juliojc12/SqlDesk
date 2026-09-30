using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlDesk.SqlAnalysis.Tests;

public class OutputRewriterTests
{
    private static string Sql(RewriteResult r) => string.Join("\nGO\n", r.Batches.Select(b => b.Text));

    private static void AssertParses(string sql)
    {
        foreach (var b in BatchSplitter.Split(sql))
        {
            new TSql160Parser(true).Parse(new StringReader(b.Text), out var errors);
            Assert.True(errors.Count == 0, $"SQL gerado não parseia: {b.Text}\n{string.Join("; ", errors.Select(e => e.Message))}");
        }
    }

    [Fact]
    public void Update_ganha_output_deleted_e_inserted()
    {
        var r = OutputRewriter.Rewrite("UPDATE dbo.Clientes SET Ativo = 0");
        var sql = Sql(r);
        Assert.Contains("OUTPUT deleted.*, inserted.*", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dbo.Clientes", sql);
        Assert.Contains("Ativo", sql);
        AssertParses(sql);
        Assert.Equal(DangerKind.UpdateWithoutWhere, Assert.Single(r.Rewritten).Kind);
        Assert.Empty(r.NotRewritten);
    }

    [Fact]
    public void Delete_ganha_output_deleted_apenas()
    {
        var sql = Sql(OutputRewriter.Rewrite("DELETE FROM dbo.Pedidos"));
        Assert.Contains("OUTPUT deleted.*", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inserted", sql, StringComparison.OrdinalIgnoreCase);
        AssertParses(sql);
    }

    [Fact]
    public void Update_com_join_sem_where_mantem_from_e_join()
    {
        var sql = Sql(OutputRewriter.Rewrite(@"UPDATE c SET c.Ativo = 0 FROM dbo.Clientes c JOIN dbo.Pedidos p ON p.ClienteId = c.Id"));
        Assert.Contains("OUTPUT deleted.*, inserted.*", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JOIN", sql, StringComparison.OrdinalIgnoreCase);
        AssertParses(sql);
        // O OUTPUT tem de vir antes do FROM (exigência da sintaxe do T-SQL)
        Assert.True(sql.IndexOf("OUTPUT", StringComparison.OrdinalIgnoreCase) < sql.IndexOf("FROM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Update_dentro_de_cte_mantem_o_with()
    {
        var sql = Sql(OutputRewriter.Rewrite("WITH x AS (SELECT Id FROM dbo.T) UPDATE dbo.T SET a = 1"));
        Assert.StartsWith("WITH", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OUTPUT", sql, StringComparison.OrdinalIgnoreCase);
        AssertParses(sql);
    }

    [Fact]
    public void Statement_com_where_nao_e_tocado()
    {
        var original = "UPDATE dbo.T SET a = 1 WHERE id = 5";
        var r = OutputRewriter.Rewrite(original);
        Assert.Equal(original, r.Batches[0].Text);
        Assert.Empty(r.Rewritten);
    }

    [Fact]
    public void Tudo_ao_redor_do_statement_reescrito_fica_intacto()
    {
        var sql = "-- cabecalho\nSELECT 1;\n  UPDATE dbo.T SET a = 1;  -- fim\nPRINT 'ok';";
        var r = OutputRewriter.Rewrite(sql);
        var text = r.Batches[0].Text;
        Assert.StartsWith("-- cabecalho\nSELECT 1;\n  ", text);
        Assert.EndsWith(";  -- fim\nPRINT 'ok';", text);
        Assert.Contains("OUTPUT", text, StringComparison.OrdinalIgnoreCase);
        AssertParses(text);
    }

    [Fact]
    public void Ponto_e_virgula_final_e_preservado_ou_ausente_como_no_original()
    {
        Assert.EndsWith(";", Sql(OutputRewriter.Rewrite("DELETE FROM dbo.T;")));
        Assert.False(Sql(OutputRewriter.Rewrite("DELETE FROM dbo.T")).EndsWith(';'));
    }

    [Fact]
    public void Varios_statements_no_mesmo_batch_sao_reescritos_sem_corromper_offsets()
    {
        var sql = "UPDATE dbo.A SET x = 1;\nDELETE FROM dbo.B;\nSELECT 1;\nUPDATE dbo.C SET y = 2 WHERE id = 1;\nDELETE FROM dbo.D;";
        var r = OutputRewriter.Rewrite(sql);
        var text = r.Batches[0].Text;
        AssertParses(text);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(text, "OUTPUT", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count);
        Assert.Contains("UPDATE dbo.C SET y = 2 WHERE id = 1;", text);
        Assert.Equal(["dbo.A", "dbo.B", "dbo.D"], r.Rewritten.Select(x => x.Target));
        Assert.Equal([1, 2, 5], r.Rewritten.Select(x => x.OriginalLine));
    }

    [Fact]
    public void Reescreve_update_dentro_de_if_e_begin_end()
    {
        var text = Sql(OutputRewriter.Rewrite("IF 1 = 1 BEGIN UPDATE dbo.T SET a = 1 END"));
        Assert.Contains("OUTPUT", text, StringComparison.OrdinalIgnoreCase);
        AssertParses(text);
    }

    [Fact]
    public void Batches_separados_por_go_so_reescrevem_o_batch_afetado()
    {
        var r = OutputRewriter.Rewrite("SELECT 1\nGO\nDELETE FROM dbo.T\nGO\nSELECT 3");
        Assert.Equal(3, r.Batches.Count);
        Assert.Equal("SELECT 1\n", r.Batches[0].Text);
        Assert.Contains("OUTPUT", r.Batches[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SELECT 3", r.Batches[2].Text);
        Assert.Equal(1, Assert.Single(r.Rewritten).BatchIndex);
    }

    [Fact]
    public void Posicao_original_dos_batches_e_preservada_para_mapear_erros()
    {
        var original = BatchSplitter.Split("SELECT 1\nGO\nDELETE FROM dbo.T");
        var r = OutputRewriter.Rewrite("SELECT 1\nGO\nDELETE FROM dbo.T");
        Assert.Equal(original.Select(b => (b.Start, b.StartLine)), r.Batches.Select(b => (b.Start, b.StartLine)));
    }

    [Fact]
    public void Statement_que_ja_tem_output_nao_e_reescrito()
    {
        var r = OutputRewriter.Rewrite("DELETE FROM dbo.T OUTPUT deleted.*");
        Assert.Equal("DELETE FROM dbo.T OUTPUT deleted.*", r.Batches[0].Text);
        Assert.Empty(r.Rewritten);
        Assert.Single(r.NotRewritten);
    }

    [Fact]
    public void Truncate_e_drop_nao_sao_reescritos_mas_ficam_na_lista()
    {
        var r = OutputRewriter.Rewrite("TRUNCATE TABLE dbo.A;\nDROP TABLE dbo.B;");
        Assert.Empty(r.Rewritten);
        Assert.Equal([DangerKind.TruncateTable, DangerKind.Drop], r.NotRewritten.Select(d => d.Kind));
        Assert.Equal("TRUNCATE TABLE dbo.A;\nDROP TABLE dbo.B;", r.Batches[0].Text);
    }

    [Fact]
    public void Update_dentro_de_exec_nao_e_reescrito()
    {
        var r = OutputRewriter.Rewrite("EXEC('UPDATE dbo.T SET a = 1')");
        Assert.Empty(r.Rewritten);
        Assert.Single(r.NotRewritten);
        Assert.Equal("EXEC('UPDATE dbo.T SET a = 1')", r.Batches[0].Text);
    }

    [Fact]
    public void Trecho_que_nao_parseia_nao_e_reescrito()
    {
        var r = OutputRewriter.Rewrite("UPDATE dbo.T SET");
        Assert.Empty(r.Rewritten);
        Assert.Equal(DangerKind.Unanalyzable, Assert.Single(r.NotRewritten).Kind);
    }

    [Fact]
    public void Script_sem_perigos_volta_identico()
    {
        var sql = "SELECT * FROM dbo.T\nGO\nINSERT INTO dbo.T VALUES (1)";
        var r = OutputRewriter.Rewrite(sql);
        Assert.Equal(BatchSplitter.Split(sql).Select(b => b.Text), r.Batches.Select(b => b.Text));
        Assert.Empty(r.Rewritten);
        Assert.Empty(r.NotRewritten);
    }

    [Fact]
    public void Identificadores_com_colchetes_e_acentos_sobrevivem_a_reescrita()
    {
        var sql = Sql(OutputRewriter.Rewrite("UPDATE [dbo].[Situação do Cliente] SET [Descrição] = 'ação'"));
        Assert.Contains("Situação do Cliente", sql);
        Assert.Contains("Descrição", sql);
        Assert.Contains("'ação'", sql);
        AssertParses(sql);
    }

    [Fact]
    public void Where_que_nao_filtra_tambem_e_reescrito()
    {
        var sql = Sql(OutputRewriter.Rewrite("UPDATE dbo.T SET a = 1 WHERE 1=1"));
        Assert.Contains("OUTPUT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 = 1", sql);
        AssertParses(sql);
    }
}
