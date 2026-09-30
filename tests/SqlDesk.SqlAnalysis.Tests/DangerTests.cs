namespace SqlDesk.SqlAnalysis.Tests;

public class DangerTests
{
    private static IReadOnlyList<DangerousStatement> Dangers(string sql) => SqlScriptAnalyzer.Analyze(sql).Dangers;

    private static DangerousStatement Single(string sql)
    {
        var d = Dangers(sql);
        Assert.Single(d);
        return d[0];
    }

    // ---------- UPDATE / DELETE ----------

    [Fact]
    public void Update_sem_where_bloqueia()
    {
        var d = Single("UPDATE dbo.Clientes SET Ativo = 0");
        Assert.Equal(DangerKind.UpdateWithoutWhere, d.Kind);
        Assert.Equal("dbo.Clientes", d.Target);
        Assert.Equal("UPDATE em dbo.Clientes sem WHERE vai afetar a tabela inteira", d.Description);
        Assert.True(d.CanPreviewWithOutput);
    }

    [Fact]
    public void Update_com_where_nao_bloqueia() =>
        Assert.Empty(Dangers("UPDATE dbo.Clientes SET Ativo = 0 WHERE Id = 5"));

    [Fact]
    public void Delete_sem_where_bloqueia()
    {
        var d = Single("DELETE FROM dbo.Pedidos");
        Assert.Equal(DangerKind.DeleteWithoutWhere, d.Kind);
        Assert.Equal("dbo.Pedidos", d.Target);
        Assert.Contains("sem WHERE", d.Description);
    }

    [Fact]
    public void Delete_com_where_nao_bloqueia() =>
        Assert.Empty(Dangers("DELETE FROM dbo.Pedidos WHERE Id = 5"));

    [Fact]
    public void Delete_top_sem_where_bloqueia() =>
        Assert.Equal(DangerKind.DeleteWithoutWhere, Single("DELETE TOP (10) FROM dbo.Pedidos").Kind);

    [Theory]
    [InlineData("UPDATE t SET a = 1 WHERE 1=1")]
    [InlineData("UPDATE t SET a = 1 WHERE 'a'='a'")]
    [InlineData("UPDATE t SET a = 1 WHERE (1 = 1)")]
    [InlineData("UPDATE t SET a = 1 WHERE 1 = 0")]                // não referencia coluna (regra da especificação)
    [InlineData("UPDATE t SET a = 1 WHERE @x = @x")]
    [InlineData("UPDATE t SET a = 1 WHERE 1=1 AND 2=2")]
    [InlineData("UPDATE t SET a = 1 WHERE id = id")]              // coluna contra ela mesma
    [InlineData("UPDATE t SET a = 1 WHERE id = 5 OR 1=1")]        // OR com tautologia
    [InlineData("UPDATE t SET a = 1 WHERE NOT (1 = 2) OR id = 5")]
    [InlineData("UPDATE t SET a = 1 WHERE 5 > 3 OR id = 7")]
    public void Where_que_nao_filtra_equivale_a_sem_where(string sql)
    {
        var d = Single(sql);
        Assert.Equal(DangerKind.UpdateWithoutWhere, d.Kind);
        Assert.Contains("não filtra", d.Description);
    }

    [Theory]
    [InlineData("UPDATE t SET a = 1 WHERE id = 5")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 5 AND 1=1")]       // a parte com coluna ainda filtra
    [InlineData("UPDATE t SET a = 1 WHERE 1=1 AND id > 10")]
    [InlineData("UPDATE t SET a = 1 WHERE id IN (1, 2)")]
    [InlineData("UPDATE t SET a = 1 WHERE t.nome LIKE 'A%'")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 5 OR id = 6")]
    [InlineData("UPDATE t SET a = 1 WHERE 1=0 OR id = 5")]
    [InlineData("UPDATE t SET a = 1 WHERE EXISTS (SELECT 1 FROM u WHERE u.id = t.id)")]
    public void Where_que_filtra_nao_bloqueia(string sql) => Assert.Empty(Dangers(sql));

    [Fact]
    public void Update_com_join_e_where_nao_bloqueia() =>
        Assert.Empty(Dangers(@"UPDATE c SET c.Ativo = 0
                               FROM dbo.Clientes c
                               JOIN dbo.Pedidos p ON p.ClienteId = c.Id
                               WHERE p.Total > 1000"));

    [Fact]
    public void Update_com_join_sem_where_bloqueia_e_resolve_o_alias()
    {
        var d = Single(@"UPDATE c SET c.Ativo = 0
                         FROM dbo.Clientes c
                         JOIN dbo.Pedidos p ON p.ClienteId = c.Id");
        Assert.Equal("dbo.Clientes", d.Target);
        Assert.Contains("UPDATE em dbo.Clientes", d.Description);
    }

    [Fact]
    public void Delete_com_alias_resolve_a_tabela_real()
    {
        var d = Single("DELETE c FROM dbo.Clientes AS c");
        Assert.Equal("dbo.Clientes", d.Target);
    }

    [Fact]
    public void Update_sem_where_dentro_de_cte_bloqueia() =>
        Assert.Equal(DangerKind.UpdateWithoutWhere, Single("WITH x AS (SELECT Id FROM dbo.T) UPDATE dbo.T SET a = 1").Kind);

    [Fact]
    public void Update_com_where_dentro_de_cte_nao_bloqueia() =>
        Assert.Empty(Dangers("WITH x AS (SELECT Id FROM dbo.T) UPDATE dbo.T SET a = 1 WHERE Id IN (SELECT Id FROM x)"));

    [Fact]
    public void Update_sem_where_dentro_de_if_bloqueia()
    {
        Assert.Equal(DangerKind.UpdateWithoutWhere, Single("IF 1 = 1 UPDATE dbo.T SET a = 1").Kind);
        Assert.Equal(DangerKind.DeleteWithoutWhere, Single("IF 1 = 1 BEGIN PRINT 'x'; DELETE FROM dbo.T; END ELSE PRINT 'y'").Kind);
    }

    [Fact]
    public void Statements_dentro_de_begin_end_while_e_procedure_sao_alcancados()
    {
        Assert.Single(Dangers("BEGIN UPDATE dbo.T SET a = 1 END"));
        Assert.Single(Dangers("WHILE 1 = 1 BEGIN DELETE FROM dbo.T END"));
        Assert.Single(Dangers("CREATE PROCEDURE dbo.Limpa AS BEGIN DELETE FROM dbo.T END"));
    }

    [Fact]
    public void Update_dentro_de_begin_try_bloqueia() =>
        Assert.Single(Dangers("BEGIN TRY UPDATE dbo.T SET a = 1 END TRY BEGIN CATCH PRINT 'x' END CATCH"));

    // ---------- palavras dentro de strings e comentários ----------

    [Theory]
    [InlineData("SELECT 'UPDATE t SET a = 1'")]
    [InlineData("SELECT 'DROP TABLE x; DELETE FROM y'")]
    [InlineData("-- UPDATE t SET a = 1\nSELECT 1")]
    [InlineData("/* DROP TABLE t */ SELECT 1")]
    [InlineData("/* TRUNCATE TABLE t\n DELETE FROM t */ SELECT 1")]
    [InlineData("SELECT 'it''s DELETE FROM t'")]
    [InlineData("SELECT [DROP], [UPDATE] FROM dbo.Palavras")]
    [InlineData("SELECT \"DELETE\" FROM dbo.T")]
    [InlineData("PRINT 'GO'\nSELECT 1")]
    public void Palavras_perigosas_em_strings_comentarios_e_identificadores_nao_bloqueiam(string sql) =>
        Assert.Empty(Dangers(sql));

    // ---------- TRUNCATE / DROP ----------

    [Fact]
    public void Truncate_bloqueia_sempre_e_traz_a_contagem()
    {
        var d = Single("TRUNCATE TABLE dbo.Clientes");
        Assert.Equal(DangerKind.TruncateTable, d.Kind);
        Assert.Equal("dbo.Clientes", d.Target);
        Assert.Equal(["SELECT COUNT_BIG(*) FROM [dbo].[Clientes]"], d.CountQueries);
        Assert.False(d.CanPreviewWithOutput);
    }

    [Theory]
    [InlineData("DROP TABLE dbo.T", "TABLE")]
    [InlineData("DROP TABLE IF EXISTS dbo.T", "TABLE")]
    [InlineData("DROP VIEW dbo.V", "VIEW")]
    [InlineData("DROP PROCEDURE dbo.P", "PROCEDURE")]
    [InlineData("DROP PROC dbo.P", "PROCEDURE")]
    [InlineData("DROP FUNCTION dbo.F", "FUNCTION")]
    [InlineData("DROP SCHEMA s", "SCHEMA")]
    [InlineData("DROP DATABASE d", "DATABASE")]
    [InlineData("DROP INDEX ix ON dbo.T", "INDEX")]
    [InlineData("DROP INDEX dbo.T.ix", "INDEX")]
    public void Cada_tipo_de_drop_bloqueia(string sql, string kind)
    {
        var d = Single(sql);
        Assert.Equal(DangerKind.Drop, d.Kind);
        Assert.StartsWith($"DROP {kind}", d.Description);
    }

    [Fact]
    public void Drop_table_com_varios_objetos_lista_todos_e_conta_cada_um()
    {
        var d = Single("DROP TABLE dbo.A, [dbo].[B]");
        Assert.Contains("dbo.A", d.Description);
        Assert.Contains("dbo.B", d.Description);
        Assert.Equal(["SELECT COUNT_BIG(*) FROM [dbo].[A]", "SELECT COUNT_BIG(*) FROM [dbo].[B]"], d.CountQueries);
    }

    [Fact]
    public void Nome_com_colchete_e_escapado_na_consulta_de_contagem()
    {
        var d = Single("TRUNCATE TABLE [dbo].[a]]b]");
        Assert.Equal("SELECT COUNT_BIG(*) FROM [dbo].[a]]b]", d.CountQueries[0]);
    }

    [Fact]
    public void Drop_de_tabela_temporaria_tambem_bloqueia() =>
        Assert.Equal(DangerKind.Drop, Single("DROP TABLE #tmp").Kind);

    [Theory]
    [InlineData("DROP TRIGGER tr")]
    [InlineData("DROP SYNONYM s")]
    [InlineData("DROP SEQUENCE dbo.sq")]
    public void Outros_drops_tambem_bloqueiam_por_seguranca(string sql) =>
        Assert.Equal(DangerKind.Drop, Single(sql).Kind);

    [Fact]
    public void Alter_table_drop_column_bloqueia_mas_drop_constraint_nao()
    {
        var d = Single("ALTER TABLE dbo.T DROP COLUMN c1, c2");
        Assert.Equal(DangerKind.DropColumn, d.Kind);
        Assert.Contains("c1, c2", d.Description);
        Assert.Empty(Dangers("ALTER TABLE dbo.T DROP CONSTRAINT ck"));
        Assert.Empty(Dangers("ALTER TABLE dbo.T DROP CONSTRAINT ck1, ck2"));
    }

    [Fact]
    public void Alter_table_misturando_constraint_e_coluna_so_bloqueia_pela_coluna()
    {
        var d = Single("ALTER TABLE dbo.T DROP CONSTRAINT ck, COLUMN c9");
        Assert.Equal(DangerKind.DropColumn, d.Kind);
        Assert.Contains("c9", d.Description);
        Assert.DoesNotContain("ck", d.Description);
    }

    [Fact]
    public void Statements_inofensivos_nao_bloqueiam()
    {
        Assert.Empty(Dangers("SELECT * FROM dbo.T"));
        Assert.Empty(Dangers("INSERT INTO dbo.T (a) VALUES (1)"));
        Assert.Empty(Dangers("CREATE TABLE dbo.N (id int)"));
        Assert.Empty(Dangers("ALTER TABLE dbo.T ADD c int"));
        Assert.Empty(Dangers("DELETE FROM dbo.T WHERE id = 1; UPDATE dbo.T SET a = 1 WHERE id = 1"));
        Assert.Empty(Dangers(""));
        Assert.Empty(Dangers("   \n\n  "));
    }

    // ---------- vários statements e batches ----------

    [Fact]
    public void Varios_perigos_no_mesmo_script_vem_em_ordem_com_a_linha()
    {
        var d = Dangers("SELECT 1;\nUPDATE dbo.A SET x = 1;\n\nDELETE FROM dbo.B;\nDROP TABLE dbo.C;");
        Assert.Equal([DangerKind.UpdateWithoutWhere, DangerKind.DeleteWithoutWhere, DangerKind.Drop], d.Select(x => x.Kind));
        Assert.Equal([2, 4, 5], d.Select(x => x.Line));
    }

    [Fact]
    public void Scripts_com_varios_batches_separados_por_go_sao_analisados_por_inteiro()
    {
        var sql = "SELECT 1\nGO\nUPDATE dbo.A SET x = 1\nGO\n\nDELETE FROM dbo.B WHERE id = 1\nGO\nTRUNCATE TABLE dbo.C\n";
        var a = SqlScriptAnalyzer.Analyze(sql);
        Assert.Equal(4, a.Batches.Count);
        Assert.Equal([DangerKind.UpdateWithoutWhere, DangerKind.TruncateTable], a.Dangers.Select(x => x.Kind));
        // a linha é do documento, não relativa ao batch
        Assert.Equal([3, 8], a.Dangers.Select(x => x.Line));
        // o offset aponta para o trecho certo do documento original
        Assert.Equal("UPDATE dbo.A SET x = 1", sql.Substring(a.Dangers[0].Start, a.Dangers[0].Length).Trim());
    }

    [Fact]
    public void Um_perigo_no_ultimo_batch_e_achado_mesmo_com_batches_limpos_antes()
    {
        var a = SqlScriptAnalyzer.Analyze("SELECT 1\nGO\nSELECT 2\nGO\nDROP TABLE dbo.X");
        Assert.Single(a.Dangers);
        Assert.False(a.IsSafe);
        Assert.Equal(5, a.Dangers[0].Line);
    }

    // ---------- erros de sintaxe ----------

    [Fact]
    public void Erro_de_sintaxe_e_reportado_com_a_linha_do_documento()
    {
        var a = SqlScriptAnalyzer.Analyze("SELECT 1\nGO\nSELECT 2\nGO\nSELECT FROM WHERE");
        Assert.NotEmpty(a.SyntaxErrors);
        Assert.All(a.SyntaxErrors, e => Assert.Equal(5, e.Line));
    }

    [Fact]
    public void Trecho_que_nao_parseia_mas_tem_update_e_tratado_como_perigoso()
    {
        var d = Single("UPDATE dbo.T SET");
        Assert.Equal(DangerKind.Unanalyzable, d.Kind);
        Assert.Contains("UPDATE", d.Description);
        Assert.False(d.CanPreviewWithOutput);
    }

    [Fact]
    public void Trecho_que_nao_parseia_e_sem_palavras_destrutivas_so_gera_erro()
    {
        var a = SqlScriptAnalyzer.Analyze("SELECT FROM WHERE");
        Assert.NotEmpty(a.SyntaxErrors);
        Assert.Empty(a.Dangers);
    }

    [Fact]
    public void Palavra_perigosa_em_string_num_trecho_invalido_nao_gera_falso_positivo() =>
        Assert.Empty(Dangers("SELECT FROM WHERE 'UPDATE DROP'"));

    // ---------- SQL dinâmico ----------

    [Fact]
    public void Exec_com_literal_e_analisado_por_dentro()
    {
        var d = Single("EXEC('DROP TABLE dbo.X')");
        Assert.Equal(DangerKind.Drop, d.Kind);
        Assert.StartsWith("Dentro de EXEC:", d.Description);
        Assert.False(d.CanPreviewWithOutput);
    }

    [Fact]
    public void Exec_com_literal_inofensivo_nao_bloqueia() =>
        Assert.Empty(Dangers("EXEC('SELECT 1')"));

    [Fact]
    public void Exec_aninhado_e_analisado_ate_o_limite()
    {
        Assert.Single(Dangers("EXEC('EXEC(''DELETE FROM dbo.T'')')"));
    }

    [Fact]
    public void Exec_com_sql_montado_so_avisa()
    {
        var a = SqlScriptAnalyzer.Analyze("DECLARE @t sysname = 'x'; EXEC('DROP TABLE ' + @t)");
        Assert.Empty(a.Dangers);
        Assert.Single(a.Warnings);
    }

    [Fact]
    public void Sp_executesql_gera_aviso()
    {
        var a = SqlScriptAnalyzer.Analyze("EXEC sp_executesql N'DELETE FROM dbo.T'");
        Assert.Single(a.Warnings);
    }

    // ---------- falsos positivos que não devem bloquear ----------

    [Theory]
    [InlineData("UPDATE STATISTICS dbo.T")]
    [InlineData("DELETE FROM dbo.T WHERE CURRENT OF meu_cursor")]
    [InlineData("MERGE dbo.A AS a USING dbo.B AS b ON a.id = b.id WHEN MATCHED THEN UPDATE SET a.x = b.x;")]
    [InlineData("SELECT u.DeleteCount, d.UpdateDate FROM dbo.Uso u JOIN dbo.D d ON d.id = u.id")]
    [InlineData("CREATE TABLE dbo.Drop2 (updateCol int, deleted bit)")]
    [InlineData("DECLARE @t TABLE (id int); INSERT @t VALUES (1)")]
    [InlineData("SELECT * INTO #tmp FROM dbo.T")]
    [InlineData("ALTER TABLE dbo.T ADD CONSTRAINT pk PRIMARY KEY (id)")]
    [InlineData("CREATE TRIGGER trg ON dbo.T AFTER UPDATE AS BEGIN SET NOCOUNT ON; END")]
    public void Nao_bloqueia_quando_nao_ha_risco(string sql) => Assert.Empty(Dangers(sql));

    // ---------- formas de esconder o perigo ----------

    [Theory]
    [InlineData("UPDATE/**/dbo.T/**/SET/**/a=1")]
    [InlineData("update dbo.t set a=1")]
    [InlineData("UpDaTe dbo.T SeT a = 1")]
    [InlineData("/* x */ -- y\n UPDATE dbo.T SET a = 1")]
    [InlineData("UPDATE [dbo].[T] SET [a] = 1")]
    [InlineData("UPDATE \"dbo\".\"T\" SET a = 1")]
    [InlineData("UPDATE\n\n\n dbo.T\n SET a = 1")]
    [InlineData("SELECT 1; UPDATE dbo.T SET a = 1")]
    [InlineData("BEGIN TRAN; UPDATE dbo.T SET a = 1; COMMIT")]
    [InlineData("UPDATE dbo.T SET a = 1 -- WHERE id = 1")]
    [InlineData("UPDATE dbo.T SET a = 'WHERE id = 1'")]
    [InlineData("UPDATE dbo.T SET a = 1 /* WHERE id = 1 */")]
    [InlineData("UPDATE TOP (1) dbo.T SET a = 1")]
    [InlineData("UPDATE srv.db.dbo.T SET a = 1")]
    [InlineData("UPDATE @tv SET a = 1")]
    public void Nao_deixa_passar_formatacao_caixa_comentarios_ou_where_falso(string sql) =>
        Assert.Single(Dangers(sql));

    [Fact]
    public void Where_dentro_de_subquery_nao_conta_como_where_do_update() =>
        Assert.Single(Dangers("UPDATE dbo.T SET a = (SELECT TOP 1 b FROM dbo.U WHERE U.id = 1)"));

    [Fact]
    public void Quebra_de_linha_crlf_nao_afeta_a_linha_reportada()
    {
        var d = Dangers("SELECT 1;\r\nSELECT 2;\r\nDELETE FROM dbo.T;");
        Assert.Equal(3, Assert.Single(d).Line);
    }

    [Fact]
    public void Posicao_do_perigo_aponta_para_o_texto_certo_com_acentos_e_emoji()
    {
        var sql = "SELECT 'ação 😀';\nDELETE FROM dbo.T;";
        var d = Assert.Single(Dangers(sql));
        Assert.Equal("DELETE FROM dbo.T;", sql.Substring(d.Start, d.Length));
    }
}
