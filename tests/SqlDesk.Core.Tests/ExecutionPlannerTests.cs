using SqlDesk.Core.Execution;

namespace SqlDesk.Core.Tests;

public class ExecutionPlannerTests
{
    private static ExecutionPlan.Runnable Runnable(ExecutionPlan p) => Assert.IsType<ExecutionPlan.Runnable>(p);

    [Fact]
    public void Sem_selecao_executa_o_statement_sob_o_cursor_e_destaca_o_trecho()
    {
        const string text = "SELECT 1;\nSELECT 2;\nSELECT 3;";
        var cursor = text.IndexOf("SELECT 2", StringComparison.Ordinal) + 3;

        var plan = Runnable(ExecutionPlanner.Plan(text, cursor, cursor, cursor, wholeScript: false));

        Assert.Equal("SELECT 2", plan.Batches.Single().Text.Trim().TrimEnd(';'));
        Assert.Equal(text.IndexOf("SELECT 2", StringComparison.Ordinal), plan.BaseOffset);
        Assert.Equal(2, plan.BaseLine);
        Assert.NotNull(plan.Highlight);
    }

    [Fact]
    public void Com_selecao_executa_exatamente_o_texto_selecionado()
    {
        const string text = "SELECT 1;\nSELECT 2;\nSELECT 3;";
        var start = text.IndexOf("SELECT 2", StringComparison.Ordinal);

        var plan = Runnable(ExecutionPlanner.Plan(text, 0, start, start + "SELECT 2;".Length, wholeScript: false));

        Assert.Equal("SELECT 2;", plan.Batches.Single().Text);
        Assert.Equal(start, plan.BaseOffset);
        Assert.Null(plan.Highlight);
    }

    [Fact]
    public void Selecao_invertida_tambem_vale()
    {
        const string text = "SELECT 1; SELECT 2;";
        var plan = Runnable(ExecutionPlanner.Plan(text, 0, 9, 0, wholeScript: false));
        Assert.Equal("SELECT 1;", plan.Batches.Single().Text);
    }

    [Fact]
    public void Script_inteiro_ignora_cursor_e_selecao_e_separa_por_GO()
    {
        const string text = "SELECT 1\nGO\nSELECT 2";

        var plan = Runnable(ExecutionPlanner.Plan(text, 3, 0, 5, wholeScript: true));

        Assert.Equal(2, plan.Batches.Count);
        Assert.Equal(0, plan.BaseOffset);
        Assert.Equal(1, plan.BaseLine);
    }

    [Fact]
    public void Cursor_em_linha_em_branco_nao_executa_nada()
    {
        const string text = "SELECT 1;\n\n\nSELECT 2;";
        var cursor = text.IndexOf("\n\n\n", StringComparison.Ordinal) + 2;

        var plan = ExecutionPlanner.Plan(text, cursor, cursor, cursor, wholeScript: false);

        Assert.IsType<ExecutionPlan.Nothing>(plan);
    }

    [Fact]
    public void Texto_vazio_ou_so_espacos_nao_executa_nada()
    {
        Assert.IsType<ExecutionPlan.Nothing>(ExecutionPlanner.Plan("   \n ", 0, 0, 0, wholeScript: true));
        Assert.IsType<ExecutionPlan.Nothing>(ExecutionPlanner.Plan("   ", 0, 0, 0, wholeScript: false));
        Assert.IsType<ExecutionPlan.Nothing>(ExecutionPlanner.Plan("SELECT 1    ", 0, 8, 12, wholeScript: false));
    }

    [Fact]
    public void UPDATE_sem_WHERE_e_recusado_com_a_linha_no_documento()
    {
        const string text = "SELECT 1;\nSELECT 2;\nUPDATE dbo.Clientes SET Nome = 'x';";

        var plan = Assert.IsType<ExecutionPlan.Dangerous>(ExecutionPlanner.Plan(text, 0, 0, 0, wholeScript: true));

        var blocked = Assert.Single(plan.Blocked);
        Assert.Equal(3, blocked.Line);
        Assert.Single(plan.Dangers);
        Assert.Equal(text, plan.Text);
    }

    [Fact]
    public void Perigo_dentro_da_selecao_tem_linha_relativa_ao_documento()
    {
        const string text = "SELECT 1;\n\nDELETE FROM dbo.T;";
        var start = text.IndexOf("DELETE", StringComparison.Ordinal);

        var plan = Assert.IsType<ExecutionPlan.Dangerous>(ExecutionPlanner.Plan(text, 0, start, text.Length, wholeScript: false));

        Assert.Equal(3, Assert.Single(plan.Blocked).Line);
        Assert.Equal(start, plan.BaseOffset);
    }

    [Fact]
    public void Perigo_fora_da_selecao_nao_bloqueia_o_trecho_selecionado()
    {
        const string text = "SELECT 1;\nDROP TABLE dbo.T;";

        var plan = Runnable(ExecutionPlanner.Plan(text, 0, 0, 9, wholeScript: false));

        Assert.Equal("SELECT 1;", plan.Batches.Single().Text);
    }

    [Fact]
    public void Statement_sob_o_cursor_perigoso_e_recusado_mesmo_sem_selecao()
    {
        const string text = "SELECT 1;\nTRUNCATE TABLE dbo.T;";
        var cursor = text.IndexOf("TRUNCATE", StringComparison.Ordinal) + 2;

        Assert.IsType<ExecutionPlan.Dangerous>(ExecutionPlanner.Plan(text, cursor, cursor, cursor, wholeScript: false));
    }

    [Fact]
    public void GO_com_repeticao_e_recusado()
    {
        var plan = Assert.IsType<ExecutionPlan.Refused>(ExecutionPlanner.Plan("INSERT INTO t VALUES (1)\nGO 5", 0, 0, 0, wholeScript: true));
        Assert.Contains("GO 5", plan.Message);
    }

    [Fact]
    public void Cursor_alem_do_fim_e_ajustado()
    {
        var plan = Runnable(ExecutionPlanner.Plan("SELECT 1", 999, 999, 999, wholeScript: false));
        Assert.Equal("SELECT 1", plan.Batches.Single().Text);
    }

    [Theory]
    [InlineData("UPDATE dbo.T SET a = 1 WHERE id = 5", true)]
    [InlineData("DELETE FROM dbo.T WHERE id = 5", true)]
    [InlineData("MERGE dbo.T AS t USING dbo.S AS s ON t.id = s.id WHEN MATCHED THEN UPDATE SET a = 1;", true)]
    [InlineData("SELECT * FROM dbo.T", false)]
    [InlineData("INSERT INTO dbo.T (a) VALUES (1)", false)]
    public void Detecta_escrita_com_filtro_para_recomendar_transacao(string sql, bool escreve)
    {
        var plan = Runnable(ExecutionPlanner.Plan(sql, 0, 0, 0, wholeScript: true));
        Assert.Equal(escreve, plan.HasWrites);
        Assert.Equal(new DocRange(0, sql.Length), plan.Range);
    }
}
