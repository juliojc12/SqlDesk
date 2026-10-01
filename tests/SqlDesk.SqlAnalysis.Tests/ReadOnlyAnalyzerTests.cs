using SqlDesk.SqlAnalysis;

namespace SqlDesk.SqlAnalysis.Tests;

public class ReadOnlyAnalyzerTests
{
    private static bool Ro(string sql) => ReadOnlyAnalyzer.IsReadOnly(sql, out _);

    [Theory]
    [InlineData("SELECT * FROM dbo.Clientes")]
    [InlineData("SELECT TOP 10 a, b FROM t WHERE x = 1 ORDER BY a")]
    [InlineData("WITH c AS (SELECT 1 AS n) SELECT * FROM c")]
    [InlineData("SELECT * FROM a UNION ALL SELECT * FROM b")]
    [InlineData("DECLARE @x int = 5; SELECT * FROM t WHERE id = @x")]
    [InlineData("SET NOCOUNT ON; SELECT 1")]
    [InlineData("SELECT 1\nGO\nSELECT 2")]
    [InlineData("SELECT 1; -- UPDATE t SET a = 1\n")]
    [InlineData("SELECT 'DROP TABLE x'")]
    public void Statements_de_leitura_podem_ser_reexecutados(string sql) => Assert.True(Ro(sql));

    [Theory]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1")]
    [InlineData("DELETE FROM t WHERE id = 1")]
    [InlineData("INSERT INTO t (a) VALUES (1)")]
    [InlineData("INSERT INTO t (a) OUTPUT inserted.a VALUES (1)")]
    [InlineData("MERGE t AS x USING s ON x.id = s.id WHEN MATCHED THEN UPDATE SET a = 1;")]
    [InlineData("EXEC dbo.pr_Faz")]
    [InlineData("SELECT * INTO #temp FROM t")]
    [InlineData("SELECT * INTO dbo.copia FROM t")]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("DROP TABLE t")]
    [InlineData("CREATE TABLE t (a int)")]
    [InlineData("BEGIN TRAN; SELECT 1")]
    [InlineData("SELECT 1; UPDATE t SET a = 1 WHERE id = 1")]
    [InlineData("SELECT 1\nGO\nDELETE FROM t WHERE id = 2")]
    public void Qualquer_coisa_que_nao_seja_so_leitura_e_recusada(string sql)
    {
        Assert.False(ReadOnlyAnalyzer.IsReadOnly(sql, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void Erro_de_sintaxe_e_recusado_por_seguranca()
    {
        Assert.False(ReadOnlyAnalyzer.IsReadOnly("SELEC * FORM t", out var reason));
        Assert.Contains("sintaxe", reason);
    }

    [Fact]
    public void Texto_vazio_nao_tem_nada_que_escreva()
    {
        Assert.True(Ro("   \n"));
    }
}
