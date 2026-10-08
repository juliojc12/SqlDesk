using SqlDesk.SqlAnalysis;

namespace SqlDesk.SqlAnalysis.Tests;

public class AiSqlGuardTests
{
    public static IEnumerable<object[]> Analyzers() =>
        [[new SqlServerAnalyzer()], [MySqlAnalyzer.Instance]];

    private static bool Ok(ISqlAnalyzer a, string sql) => AiSqlGuard.Check(a, sql, out _);

    [Theory, MemberData(nameof(Analyzers))]
    public void Aceita_select_simples_com_ou_sem_ponto_e_virgula_final(ISqlAnalyzer a)
    {
        Assert.True(Ok(a, "SELECT id, nome FROM clientes WHERE ativo = 1"));
        Assert.True(Ok(a, "SELECT 1;"));
        Assert.True(Ok(a, "-- comentário\nSELECT 1"));
        Assert.True(Ok(a, "WITH x AS (SELECT 1 AS a) SELECT a FROM x"));
    }

    [Theory, MemberData(nameof(Analyzers))]
    public void Recusa_escrita_e_estrutura(ISqlAnalyzer a)
    {
        Assert.False(Ok(a, "UPDATE t SET a = 1"));
        Assert.False(Ok(a, "DELETE FROM t"));
        Assert.False(Ok(a, "DROP TABLE t"));
        Assert.False(Ok(a, "INSERT INTO t VALUES (1)"));
        Assert.False(Ok(a, "TRUNCATE TABLE t"));
        Assert.False(Ok(a, "EXEC sp_who"));
    }

    [Theory, MemberData(nameof(Analyzers))]
    public void Recusa_mais_de_um_comando_e_vazio(ISqlAnalyzer a)
    {
        Assert.False(Ok(a, "SELECT 1; DELETE FROM t"));
        Assert.False(Ok(a, "SELECT 1; SELECT 2"));
        Assert.False(Ok(a, ""));
        Assert.False(Ok(a, "   "));
        Assert.False(Ok(a, "-- só comentário"));
    }

    [Theory, MemberData(nameof(Analyzers))]
    public void Recusa_comandos_que_nao_sao_consulta(ISqlAnalyzer a)
    {
        Assert.False(Ok(a, "USE outro"));
        Assert.False(Ok(a, "SET @a = 1"));
        Assert.False(Ok(a, "SHOW TABLES"));
        Assert.False(Ok(a, "DECLARE @a int = 1; SELECT @a"));
    }

    [Theory, MemberData(nameof(Analyzers))]
    public void Recusa_select_com_efeito_colateral(ISqlAnalyzer a)
    {
        Assert.False(Ok(a, "SELECT * INTO nova FROM t"));
        Assert.False(Ok(a, "SELECT * FROM OPENROWSET('x','y','z')"));
        Assert.False(Ok(a, "SELECT SLEEP(10)"));
        Assert.False(Ok(a, "SELECT LOAD_FILE('/etc/passwd')"));
        Assert.False(Ok(a, "SELECT * FROM t INTO OUTFILE '/tmp/x'"));
        Assert.False(Ok(a, "SELECT xp_cmdshell('dir')"));
        Assert.False(Ok(a, "SELECT * FROM t FOR UPDATE"));
        Assert.False(Ok(a, "SELECT /*!50000 1 */"));
    }

    [Fact]
    public void Informa_o_motivo()
    {
        Assert.False(AiSqlGuard.Check(new SqlServerAnalyzer(), "DELETE FROM t", out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }
}
