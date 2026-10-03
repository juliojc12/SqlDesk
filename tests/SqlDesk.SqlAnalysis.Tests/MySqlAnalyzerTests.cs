using SqlDesk.SqlAnalysis;

namespace SqlDesk.SqlAnalysis.Tests;

public class MySqlAnalyzerTests
{
    private static readonly ISqlAnalyzer A = MySqlAnalyzer.Instance;

    private static DangerKind[] Kinds(string sql) => A.Analyze(sql).Dangers.Select(d => d.Kind).ToArray();

    [Theory]
    [InlineData("UPDATE t SET a = 1", DangerKind.UpdateWithoutWhere)]
    [InlineData("update `t` set a=1 limit 10", DangerKind.UpdateWithoutWhere)]
    [InlineData("DELETE FROM t", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE FROM db.t WHERE 1=1", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE FROM t WHERE TRUE", DangerKind.DeleteWithoutWhere)]
    [InlineData("UPDATE t SET a=1 WHERE 'x'='x'", DangerKind.UpdateWithoutWhere)]
    [InlineData("TRUNCATE TABLE t", DangerKind.TruncateTable)]
    [InlineData("TRUNCATE t", DangerKind.TruncateTable)]
    [InlineData("DROP TABLE t", DangerKind.Drop)]
    [InlineData("DROP DATABASE d", DangerKind.Drop)]
    [InlineData("ALTER TABLE t DROP COLUMN c", DangerKind.DropColumn)]
    [InlineData("ALTER TABLE t ADD COLUMN c INT", DangerKind.AlterTable)]
    public void Detecta_perigosos(string sql, DangerKind kind) => Assert.Equal([kind], Kinds(sql));

    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("UPDATE t SET a=1 WHERE id = 5")]
    [InlineData("DELETE FROM t WHERE id IN (1,2)")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("SELECT 'DELETE FROM t'")]
    [InlineData("SELECT 1 -- DELETE FROM t")]
    [InlineData("SELECT 1 # DROP TABLE t")]
    [InlineData("SELECT /* TRUNCATE t */ 1")]
    [InlineData("SELECT `drop` FROM t")]
    [InlineData("UPDATE t SET a='a; DELETE FROM x' WHERE id=1")]
    public void Nao_acusa_o_que_e_seguro(string sql) => Assert.Empty(Kinds(sql));

    [Fact]
    public void Ponto_e_virgula_em_string_nao_separa_e_nao_esconde_perigo()
    {
        var a = A.Analyze("SELECT ';'; DELETE FROM t; SELECT 2");
        Assert.Single(a.Dangers);
        Assert.Equal(DangerKind.DeleteWithoutWhere, a.Dangers[0].Kind);
        Assert.Equal(3, a.Batches.Count);
    }

    [Fact]
    public void Comentario_executavel_conta_como_codigo() =>
        Assert.Equal([DangerKind.DeleteWithoutWhere], Kinds("/*! DELETE FROM t */"));

    [Fact]
    public void Traco_traco_sem_espaco_nao_e_comentario() =>
        // "--1" em MySQL é "- -1": o resto da linha é código.
        Assert.Equal([DangerKind.DeleteWithoutWhere], Kinds("SELECT 1 --1\n; DELETE FROM t"));

    [Fact]
    public void Delimiter_muda_o_terminador()
    {
        var sql = "DELIMITER $$\nCREATE PROCEDURE p() BEGIN DELETE FROM t WHERE id=1; END$$\nDELIMITER ;\nSELECT 1;";
        var a = A.Analyze(sql);
        Assert.Empty(a.Dangers);
        Assert.Equal(2, a.Batches.Count);
    }

    [Fact]
    public void Trecho_nao_analisavel_com_palavra_destrutiva_e_perigoso() =>
        Assert.Equal([DangerKind.Unanalyzable], Kinds("DELETE FROM t WHERE a = 'aberta"));

    [Fact]
    public void Gera_contagem_previa()
    {
        var d = A.Analyze("DELETE FROM `db`.`t`").Dangers.Single();
        Assert.Equal(["SELECT COUNT(*) FROM `db`.`t`"], d.CountQueries);
        Assert.False(d.CanPreviewWithOutput);
    }

    [Fact]
    public void Marca_escrita_com_where() => Assert.True(A.Analyze("UPDATE t SET a=1 WHERE id=1").HasWrites);

    [Fact]
    public void Localiza_statement_sob_o_cursor()
    {
        const string sql = "SELECT 1;\nSELECT ';' AS x;\nSELECT 3";
        var r = A.Locate(sql, sql.IndexOf("AS x", StringComparison.Ordinal));
        Assert.Equal("SELECT ';' AS x", sql.Substring(r.Range!.Start, r.Range.Length));
    }

    [Fact]
    public void Localiza_em_linha_em_branco_nao_acha()
    {
        var r = A.Locate("SELECT 1;\n\nSELECT 2", 10);
        Assert.False(r.Found);
    }

    [Theory]
    [InlineData("SELECT 1", true)]
    [InlineData("SHOW TABLES", true)]
    [InlineData("DESCRIBE t", true)]
    [InlineData("EXPLAIN SELECT 1", true)]
    [InlineData("WITH c AS (SELECT 1) SELECT * FROM c", true)]
    [InlineData("SELECT * FROM t INTO OUTFILE 'x'", false)]
    [InlineData("SELECT * FROM t FOR UPDATE", false)]
    [InlineData("INSERT INTO t VALUES (1)", false)]
    [InlineData("CALL p()", false)]
    public void Somente_leitura(string sql, bool expected) => Assert.Equal(expected, A.IsReadOnly(sql, out _));

    [Fact]
    public void Rewrite_nao_altera_o_texto()
    {
        var r = A.Rewrite("DELETE FROM t");
        Assert.Empty(r.Rewritten);
        Assert.Single(r.NotRewritten);
        Assert.Equal("DELETE FROM t", r.Batches.Single().Text);
    }

    // ---- Casos extras: pontos de revisão que os casos acima não fixam ----

    [Theory]
    [InlineData("UPDATE t SET a=1 WHERE 1=1 LIMIT 5", DangerKind.UpdateWithoutWhere)]
    [InlineData("DELETE FROM t ORDER BY id LIMIT 1", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE FROM t WHERE 1=1 OR id = 5", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE t FROM t JOIN (SELECT id FROM u WHERE x = 1) s ON s.id = t.id", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE FROM t USING t, (SELECT id FROM u WHERE x = 1) s", DangerKind.DeleteWithoutWhere)]
    [InlineData("UPDATE t SET a = (SELECT b FROM u WHERE u.id = 1)", DangerKind.UpdateWithoutWhere)]
    [InlineData("dElEtE FrOm t", DangerKind.DeleteWithoutWhere)]
    [InlineData("TrUnCaTe TaBlE t", DangerKind.TruncateTable)]
    [InlineData("ALTER TABLE t DROP c", DangerKind.DropColumn)]
    [InlineData("/*!50001 DROP TABLE t */", DangerKind.Drop)]
    [InlineData("/*M!100000 DROP TABLE t */", DangerKind.Drop)]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM t", DangerKind.DeleteWithoutWhere)]
    [InlineData("PREPARE s FROM 'DROP TABLE t'", DangerKind.Drop)]
    [InlineData("CREATE OR REPLACE TABLE t (a INT)", DangerKind.Drop)]
    public void Detecta_perigosos_extras(string sql, DangerKind kind) => Assert.Equal([kind], Kinds(sql));

    [Theory]
    [InlineData("DELETE FROM t WHERE id IN (SELECT 1)")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1 ORDER BY id LIMIT 1")]
    [InlineData("SELECT 1 /*! , 2 */")]
    [InlineData("UPDATE t SET a = 'it\\'s' WHERE id = 1")]
    public void Nao_acusa_o_que_e_seguro_extras(string sql) => Assert.Empty(Kinds(sql));

    [Theory]
    [InlineData("SELECT `x FROM t; DROP TABLE t")]
    [InlineData("SELECT 1 /* DROP TABLE t")]
    [InlineData("/* DROP TABLE t")]
    [InlineData("SELECT 1; /*! DROP TABLE t")]
    [InlineData("DELETE FROM t WHERE id = (1")]
    [InlineData("DELIMITER\nDROP TABLE t")]
    [InlineData("BEGIN NOT ATOMIC DELETE FROM t WHERE id = 1; END")]
    public void Trecho_aberto_ou_malformado_com_palavra_destrutiva_e_perigoso(string sql) =>
        Assert.Contains(DangerKind.Unanalyzable, Kinds(sql));

    [Fact]
    public void Delimiter_customizado_nao_esconde_ponto_e_virgula() =>
        Assert.Equal([DangerKind.Drop], Kinds("DELIMITER $$\nSELECT 1; DROP TABLE t$$"));

    [Fact]
    public void Sem_escape_por_barra_a_string_pode_fechar_antes() =>
        // Com NO_BACKSLASH_ESCAPES, 'a\' é uma string completa e o DROP executa.
        Assert.NotEmpty(Kinds("SELECT 'a\\'; DROP TABLE t; -- '"));

    [Fact]
    public void Posicao_aponta_o_statement_e_nao_o_comentario()
    {
        const string sql = "-- comentário\n/* bloco */\n  DELETE FROM t";
        var d = A.Analyze(sql).Dangers.Single();
        Assert.Equal(sql.IndexOf("DELETE", StringComparison.Ordinal), d.Start);
        Assert.Equal("DELETE FROM t".Length, d.Length);
        Assert.Equal(3, d.Line);
    }

    [Theory]
    [InlineData("SET @a = 1", true)]
    [InlineData("SET @@global.max_connections = 1", false)]
    [InlineData("SET @a = 1, sql_mode = ''", false)]
    [InlineData("SELECT * FROM t LOCK IN SHARE MODE", false)]
    [InlineData("EXPLAIN ANALYZE DELETE FROM t", false)]
    [InlineData("SELECT 1 /* aberto", false)]
    public void Somente_leitura_extras(string sql, bool expected) => Assert.Equal(expected, A.IsReadOnly(sql, out _));

    // ---- Rodada de correção 1 (revisão adversarial) ----

    [Theory]
    [InlineData("SELECT 1 /*+ ' */ , 2; DROP TABLE t; -- ' */")]
    [InlineData("SELECT 1 /*M! ' */ , 2; DROP TABLE t; -- ' */")]
    [InlineData("SELECT 1 /*!99999 ' */ , 2; DROP TABLE t; -- ' */")]
    public void Aspas_em_comentario_que_o_servidor_ignora_nao_escondem_drop(string sql)
    {
        Assert.NotEmpty(Kinds(sql));
        Assert.False(A.IsReadOnly(sql, out _));
    }

    [Theory]
    [InlineData("EXECUTE IMMEDIATE 'DROP TABLE t'", DangerKind.Drop)]
    [InlineData("EXECUTE IMMEDIATE @sql", DangerKind.Unanalyzable)]
    [InlineData("ANALYZE DELETE FROM t", DangerKind.DeleteWithoutWhere)]
    [InlineData("EXPLAIN ANALYZE DELETE t1 FROM t1 JOIN t2 ON t1.id=t2.id", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE FROM t WHERE (id = 5 OR 1=1)", DangerKind.DeleteWithoutWhere)]
    [InlineData("CREATE EVENT e ON SCHEDULE AT CURRENT_TIMESTAMP DO DELETE FROM t", DangerKind.DeleteWithoutWhere)]
    public void Prefixos_que_executam_e_tautologia_entre_parenteses(string sql, DangerKind kind) => Assert.Equal([kind], Kinds(sql));

    [Theory]
    [InlineData("EXPLAIN DELETE FROM t WHERE id = 1")]
    [InlineData("EXPLAIN DELETE FROM t")]
    [InlineData("ANALYZE TABLE t")]
    [InlineData("DELETE FROM t WHERE (id = 5 OR id = 6) AND 1=1")]
    [InlineData("CREATE EVENT e ON SCHEDULE AT CURRENT_TIMESTAMP DO DELETE FROM t WHERE id = 1")]
    [InlineData("SELECT /*+ BKA(t) */ * FROM t")]
    [InlineData("EXPLAIN ANALYZE SELECT * FROM t FOR UPDATE")]
    public void Prefixos_seguros_nao_sao_acusados(string sql) => Assert.Empty(Kinds(sql));

    [Theory]
    [InlineData("CREATE TABLE x (id INT)")]
    [InlineData("create index ix on t(id)")]
    [InlineData("ALTER TABLE t ADD c INT")]
    [InlineData("DROP TABLE t")]
    [InlineData("RENAME TABLE a TO b")]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("START TRANSACTION")]
    [InlineData("BEGIN")]
    [InlineData("BEGIN WORK")]
    [InlineData("BEGIN NOT ATOMIC DELETE FROM t; END")]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    [InlineData("SET autocommit = 0")]
    [InlineData("SET SESSION autocommit=1")]
    [InlineData("SET @@autocommit = 0")]
    [InlineData("SET @a = 1, autocommit = 0")]
    [InlineData("LOCK TABLES t WRITE")]
    [InlineData("UNLOCK TABLES")]
    [InlineData("GRANT SELECT ON *.* TO u")]
    [InlineData("REVOKE SELECT ON *.* FROM u")]
    [InlineData("FLUSH PRIVILEGES")]
    [InlineData("ANALYZE TABLE t")]
    [InlineData("OPTIMIZE TABLE t")]
    [InlineData("REPAIR TABLE t")]
    [InlineData("CHECK TABLE t")]
    [InlineData("CACHE INDEX t IN hot")]
    [InlineData("LOAD INDEX INTO CACHE t")]
    [InlineData("RESET QUERY CACHE")]
    [InlineData("INSTALL PLUGIN p SONAME 'p.so'")]
    [InlineData("UNINSTALL PLUGIN p")]
    [InlineData("XA START 'x'")]
    [InlineData("CHANGE MASTER TO MASTER_HOST='h'")]
    [InlineData("STOP SLAVE")]
    [InlineData("CALL proc()")]
    [InlineData("EXECUTE stmt")]
    [InlineData("PREPARE stmt FROM 'COMMIT'")]
    [InlineData("lbl: LOOP LEAVE lbl; END LOOP")]
    [InlineData("IF 1 THEN COMMIT; END IF")]
    [InlineData("DELETE FROM t; COMMIT")]
    [InlineData("/*!50000 COMMIT */")]
    [InlineData("SELECT 'abc")] // quebrado: opaco, assume o pior
    public void Comandos_que_confirmam_sozinhos_ou_sao_opacos(string sql) =>
        Assert.True(MySqlAnalyzer.Instance.CausesImplicitCommit(sql));

    [Theory]
    [InlineData("DELETE FROM t")]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("SELECT * FROM t")]
    [InlineData("SET @a = 1")]
    [InlineData("SET NAMES utf8mb4")]
    [InlineData("SAVEPOINT s")]
    [InlineData("ROLLBACK TO SAVEPOINT s")]
    [InlineData("ROLLBACK WORK TO s")]
    [InlineData("RELEASE SAVEPOINT s")]
    [InlineData("ANALYZE DELETE FROM t")]
    [InlineData("SELECT 'COMMIT; CREATE TABLE x (id INT)'")]
    [InlineData("-- COMMIT\nSELECT 1")]
    [InlineData("")]
    public void Comandos_que_nao_confirmam_sozinhos(string sql) =>
        Assert.False(MySqlAnalyzer.Instance.CausesImplicitCommit(sql));

    [Theory]
    [InlineData("SET PASSWORD = 'x'")]
    [InlineData("SET PASSWORD FOR u = 'x'")]
    [InlineData("SET DEFAULT ROLE r TO u")]
    [InlineData("SET STATEMENT max_statement_time=100 FOR CREATE TABLE x (id INT)")]
    [InlineData("set statement max_statement_time=1, sql_mode='' for commit")]
    [InlineData("SET STATEMENT a=1 FOR SET STATEMENT b=2 FOR COMMIT")]
    [InlineData("SET STATEMENT max_statement_time=1")]       // sem FOR: opaco
    [InlineData("SET STATEMENT max_statement_time=1 FOR")]   // FOR sem comando: opaco
    [InlineData("SET STATEMENT max_statement_time=1 FOR SET PASSWORD = 'x'")]
    public void SET_que_confirma_sozinho(string sql) => Assert.True(A.CausesImplicitCommit(sql));

    [Theory]
    [InlineData("SET @x=1")]
    [InlineData("SET NAMES utf8mb4")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL READ COMMITTED")]
    [InlineData("SET ROLE x")]
    [InlineData("SET @password = 1")]
    [InlineData("SET STATEMENT max_statement_time=1 FOR SELECT 1")]
    [InlineData("SET STATEMENT max_statement_time=1 FOR DELETE FROM t WHERE id = 1")]
    public void SET_que_nao_confirma_sozinho(string sql) => Assert.False(A.CausesImplicitCommit(sql));

    [Fact]
    public void SET_STATEMENT_FOR_DELETE_sem_where_e_perigoso_como_o_DELETE()
    {
        var d = A.Analyze("SET STATEMENT max_statement_time=1 FOR DELETE FROM t").Dangers.Single();
        Assert.Equal(DangerKind.DeleteWithoutWhere, d.Kind);
        Assert.Equal("t", d.Target);
        Assert.Equal(["SELECT COUNT(*) FROM t"], d.CountQueries);
    }

    private static string Nested(int levels, string inner) => string.Concat(Enumerable.Repeat("SET STATEMENT a=1 FOR ", levels)) + inner;

    [Fact]
    public void SET_STATEMENT_aninhado_demais_nao_derruba_o_processo_e_e_tratado_como_opaco()
    {
        // 5000 níveis (~110 KB colados): antes estourava a pilha (erro que não dá para capturar e mata o processo).
        var sql = Nested(5000, "SELECT 1");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.True(A.CausesImplicitCommit(sql));
        Assert.Equal([DangerKind.Unanalyzable], Kinds(sql));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"demorou {watch.Elapsed}");
    }

    [Theory]
    [InlineData(31, false)]
    [InlineData(32, false)] // limite: até 32 níveis o comando interno é analisado normalmente
    [InlineData(33, true)]  // acima do limite: opaco (confirma sozinho e é perigoso por segurança)
    public void SET_STATEMENT_aninhado_no_limite(int levels, bool opaque)
    {
        var sql = Nested(levels, "SELECT 1");
        Assert.Equal(opaque, A.CausesImplicitCommit(sql));
        Assert.Equal(opaque ? [DangerKind.Unanalyzable] : [], Kinds(sql));
    }

    [Fact]
    public void SET_STATEMENT_aninhado_dentro_do_limite_ainda_ve_o_comando_interno()
    {
        Assert.Equal([DangerKind.DeleteWithoutWhere], Kinds(Nested(32, "DELETE FROM t")));
        Assert.True(A.CausesImplicitCommit(Nested(32, "DROP TABLE x")));
        Assert.Equal([DangerKind.Drop], Kinds(Nested(3, "DROP TABLE x")));
    }

    [Fact]
    public void SET_STATEMENT_FOR_DELETE_aponta_o_trecho_certo_no_documento()
    {
        var sql = "SELECT 1;\nSET STATEMENT a=1 FOR DELETE FROM t";
        var d = A.Analyze(sql).Dangers.Single();
        Assert.Equal(DangerKind.DeleteWithoutWhere, d.Kind);
        Assert.Equal(10, d.Start);
        Assert.Equal(sql.Length - 10, d.Length);
        Assert.Equal(2, d.Line);
    }

    // ---- Revisão final: espaço Unicode (o servidor só trata espaço ASCII e caracteres de controle como espaço) ----

    [Fact]
    public void Traco_traco_seguido_de_espaco_unicode_nao_e_comentario_e_nao_esconde_drop()
    {
        // No servidor, "-- x" é "- - x" (U+00A0 é caractere de identificador): o DROP executa.
        const string sql = "SELECT 1 -- x FROM (SELECT 1 AS ` x`) z; DROP TABLE zz_rv_v; -- \nSELECT 2";
        Assert.Contains(DangerKind.Drop, Kinds(sql));
        Assert.False(A.IsReadOnly(sql, out _));
    }

    [Fact]
    public void Espaco_unicode_antes_do_WHERE_faz_parte_do_alias_e_o_UPDATE_fica_sem_WHERE()
    {
        // "zz_rv_t  WHERE" é a tabela com o alias " WHERE": o servidor atualiza todas as linhas.
        const string sql = "UPDATE zz_rv_t  WHERE SET id = 9";
        Assert.Equal([DangerKind.UpdateWithoutWhere], Kinds(sql));
        Assert.False(A.IsReadOnly(sql, out _));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(" ")]
    [InlineData("　")]
    public void Espaco_unicode_entre_tokens_e_caractere_de_palavra(string space)
    {
        Assert.Equal([DangerKind.DeleteWithoutWhere], Kinds($"DELETE FROM t {space}WHERE id = 1"));
        Assert.Contains(DangerKind.Drop, Kinds($"SELECT 1 --{space}x; DROP TABLE t"));
        // Colado na palavra, vira parte dela: "DELETE　FROM" não é DELETE.
        Assert.Empty(Kinds($"SELECT 1;{space}DELETE{space}FROM t"));
    }

    [Theory]
    [InlineData("SELECT 1 -- DROP TABLE t")]
    [InlineData("SELECT 1 --\tDROP TABLE t")]
    [InlineData("SELECT 1 --\u000bDROP TABLE t")]
    [InlineData("SELECT 1 --\u0001DROP TABLE t")]
    [InlineData("SELECT 1 --")]
    [InlineData("SELECT\t1\r\n\fFROM\u000bt")]
    public void Espaco_e_controle_ASCII_continuam_como_antes(string sql)
    {
        Assert.Empty(Kinds(sql));
        Assert.True(A.IsReadOnly(sql, out _));
    }

    [Fact]
    public void Versao_do_comentario_executavel_so_aceita_digitos_ASCII() =>
        // O dígito arábico-índico não é versão para o servidor: "٥WHERE" é um alias e o UPDATE fica sem WHERE (e, por ter
        // comentário executável, o comando também é não analisável).
        Assert.Equal([DangerKind.UpdateWithoutWhere, DangerKind.Unanalyzable], Kinds("UPDATE t /*!٥WHERE */ SET id = 9"));

    // ---- Revisão final: leituras alternativas rodam mesmo quando a principal já achou perigo ----

    [Theory]
    [InlineData("DELETE FROM zz_rv_t /*!99999 ' */ ; DROP TABLE zz_rv_v; -- ' */")]
    [InlineData("DELETE FROM t WHERE 1=1 OR c='x\\'; DROP TABLE victim; -- '")]
    public void Leitura_alternativa_acusa_o_DROP_escondido_atras_de_outro_perigo(string sql)
    {
        var a = A.Analyze(sql);
        var drop = sql.IndexOf("DROP", StringComparison.Ordinal);
        Assert.True(a.Dangers.Count >= 2, string.Join(", ", a.Dangers.Select(d => d.Kind)));
        // Algum perigo além do principal cobre o DROP (diálogo e "irreversível" o incluem).
        Assert.Contains(a.Dangers, d => d.Kind == DangerKind.Unanalyzable && d.Start <= drop && drop < d.Start + d.Length);
        Assert.True(A.CausesImplicitCommit(sql));
    }

    [Theory]
    [InlineData("UPDATE t SET a = 'it\\'s'", DangerKind.UpdateWithoutWhere)]
    [InlineData("/*!40101 SET NAMES utf8 */; DELETE FROM t", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE FROM t WHERE a = 'c:\\\\dir'; DROP TABLE x", DangerKind.Drop)]
    public void Leitura_alternativa_nao_duplica_nem_inventa_perigo(string sql, DangerKind main)
    {
        var kinds = Kinds(sql);
        Assert.Contains(main, kinds);
        Assert.DoesNotContain(DangerKind.Unanalyzable, kinds);
    }

    [Theory]
    [InlineData("/*!40101 SET NAMES utf8 */; DELETE FROM t WHERE id = 1")]
    [InlineData("/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE */; UPDATE t SET a = 'it\\'s' WHERE id = 1")]
    [InlineData("SELECT /*+ BKA(t) */ * FROM t WHERE a = 'x\\\\'")]
    public void Leitura_alternativa_nao_acusa_script_comum(string sql) => Assert.Empty(Kinds(sql));

    // ---- Revisão final: contagem prévia só com nome de verdade (palavra ou crase) ----

    [Theory]
    [InlineData("DELETE FROM 't\\'; DROP TABLE victim; -- '")]
    [InlineData("DELETE FROM \"t\\\"; DROP TABLE victim; -- \"")]
    [InlineData("TRUNCATE TABLE 'x'")]
    [InlineData("DROP TABLE \"x\"")]
    [InlineData("UPDATE 'a' SET b = 1")]
    [InlineData("DELETE FROM db.'t'")]
    [InlineData("DELETE FROM @x")]
    [InlineData("CREATE OR REPLACE TABLE \"t\" (a INT)")]
    public void Alvo_que_nao_e_identificador_nao_gera_contagem(string sql)
    {
        var a = A.Analyze(sql);
        Assert.NotEmpty(a.Dangers);
        Assert.All(a.Dangers, d =>
        {
            Assert.Empty(d.CountQueries);
            Assert.Null(d.Target);
        });
    }

    [Theory]
    [InlineData("DELETE FROM db.t", "db.t")]
    [InlineData("DELETE FROM `db`.`t`", "`db`.`t`")]
    [InlineData("DELETE FROM `we``ird`", "`we``ird`")]
    [InlineData("TRUNCATE TABLE `a b`.c$1", "`a b`.c$1")]
    [InlineData("DROP TABLE IF EXISTS tabela_ção", "tabela_ção")]
    [InlineData("UPDATE LOW_PRIORITY t SET a = 1", "t")]
    public void Contagem_previa_exata_para_nome_valido(string sql, string target)
    {
        var d = A.Analyze(sql).Dangers.Single();
        Assert.Equal(target, d.Target);
        Assert.Equal([$"SELECT COUNT(*) FROM {target}"], d.CountQueries);
    }

    // ---- Acompanhamento: UPDATE/DELETE com mais de uma tabela não têm um alvo só (N1) ----

    [Theory]
    [InlineData("DELETE m FROM i JOIN m ON i.id = m.id", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE m, i FROM i JOIN m ON i.id = m.id WHERE 1=1", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE m.* FROM i, m", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE LOW_PRIORITY QUICK IGNORE m FROM i JOIN m", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE FROM m USING i JOIN m ON i.id = m.id", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE FROM i, m USING i JOIN m ON i.id = m.id", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE FROM i, m", DangerKind.DeleteWithoutWhere)]
    [InlineData("DELETE FROM i JOIN m ON i.id = m.id", DangerKind.DeleteWithoutWhere)]
    [InlineData("UPDATE i, m SET m.v = 7", DangerKind.UpdateWithoutWhere)]
    [InlineData("UPDATE i JOIN m ON i.id = m.id SET m.v = 7", DangerKind.UpdateWithoutWhere)]
    [InlineData("UPDATE i INNER JOIN m ON i.id = m.id SET m.v = 7 WHERE 1=1", DangerKind.UpdateWithoutWhere)]
    [InlineData("UPDATE i LEFT JOIN m USING (id) SET m.v = 7", DangerKind.UpdateWithoutWhere)]
    [InlineData("UPDATE i LEFT OUTER JOIN m ON i.id = m.id SET m.v = 7", DangerKind.UpdateWithoutWhere)]
    [InlineData("UPDATE i RIGHT JOIN m ON i.id = m.id SET m.v = 7", DangerKind.UpdateWithoutWhere)]
    [InlineData("UPDATE i CROSS JOIN m SET m.v = 7", DangerKind.UpdateWithoutWhere)]
    [InlineData("UPDATE i NATURAL JOIN m SET m.v = 7", DangerKind.UpdateWithoutWhere)]
    [InlineData("UPDATE i STRAIGHT_JOIN m ON i.id = m.id SET m.v = 7", DangerKind.UpdateWithoutWhere)]
    [InlineData("UPDATE LOW_PRIORITY IGNORE i AS a, m AS b SET b.v = 7", DangerKind.UpdateWithoutWhere)]
    [InlineData("UPDATE (SELECT id FROM i) s JOIN m ON s.id = m.id SET m.v = 7", DangerKind.UpdateWithoutWhere)]
    [InlineData("UPDATE (i JOIN m ON i.id = m.id) SET m.v = 7", DangerKind.UpdateWithoutWhere)]
    [InlineData("WITH c AS (SELECT 1 AS id) UPDATE i JOIN c ON i.id = c.id SET i.v = 1", DangerKind.UpdateWithoutWhere)]
    public void UPDATE_DELETE_com_mais_de_uma_tabela_ficam_sem_alvo(string sql, DangerKind kind)
    {
        var d = A.Analyze(sql).Dangers.Single();
        Assert.Equal(kind, d.Kind);
        Assert.Null(d.Target); // sem alvo único: sem contagem e irreversível ("alvo não reconhecido")
        Assert.Empty(d.CountQueries);
        Assert.Contains("mais de uma tabela", d.Description);
    }

    [Theory]
    [InlineData("DELETE FROM t", "t")]
    [InlineData("DELETE FROM db.t", "db.t")]
    [InlineData("DELETE LOW_PRIORITY FROM t", "t")]
    [InlineData("DELETE LOW_PRIORITY QUICK IGNORE FROM t", "t")]
    [InlineData("DELETE FROM t PARTITION (p0, p1)", "t")]
    [InlineData("DELETE FROM t AS x WHERE 1=1", "t")]
    [InlineData("DELETE FROM t ORDER BY a, b LIMIT 1", "t")]
    [InlineData("DELETE FROM t RETURNING a, b", "t")]
    [InlineData("DELETE FROM t WHERE 1=1 OR id IN (SELECT id FROM x JOIN y ON x.a = y.a)", "t")]
    [InlineData("UPDATE t SET a = 1", "t")]
    [InlineData("UPDATE LOW_PRIORITY IGNORE t SET a = 1", "t")]
    [InlineData("UPDATE t SET a = (SELECT 1 FROM x JOIN y ON x.id = y.id) WHERE 1=1", "t")]
    [InlineData("UPDATE t SET a = CONCAT(b, c)", "t")]
    [InlineData("UPDATE t SET a = 1, b = 2", "t")]
    [InlineData("UPDATE t PARTITION (p0, p1) SET a = 1", "t")]
    [InlineData("UPDATE t SET a = 1 WHERE 1=1 OR id IN (SELECT id FROM x, y)", "t")]
    public void UPDATE_DELETE_de_uma_tabela_continuam_com_o_alvo(string sql, string target)
    {
        var d = A.Analyze(sql).Dangers.Single();
        Assert.Equal(target, d.Target);
        Assert.Equal([$"SELECT COUNT(*) FROM {target}"], d.CountQueries);
        Assert.DoesNotContain("mais de uma tabela", d.Description);
    }

    // ---- Acompanhamento: comentário executável que troca o alvo (N2) ----

    [Theory]
    [InlineData("DELETE FROM /*!99999 i */ m")]
    [InlineData("DELETE FROM /*M!999999 i */ m")]
    [InlineData("UPDATE /*!99999 i */ m SET v = 1")]
    [InlineData("DROP TABLE /*!99999 a */ b")]
    [InlineData("DROP TABLE /*M!999999 a */ b")]
    [InlineData("TRUNCATE TABLE /*!99999 a */ b")]
    public void Comentario_executavel_que_troca_o_alvo_e_nao_analisavel(string sql)
    {
        var a = A.Analyze(sql);
        Assert.Contains(a.Dangers, d => d.Kind == DangerKind.Unanalyzable && d.Start == 0);
    }

    [Theory]
    [InlineData("/*!40101 SET NAMES utf8 */; DELETE FROM t")]
    [InlineData("/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE */; DROP TABLE t")]
    public void Comentario_executavel_em_statement_proprio_nao_acusa_mais_nada(string sql) =>
        Assert.DoesNotContain(DangerKind.Unanalyzable, Kinds(sql));

    // ---- Rodada 1 do acompanhamento: o servidor executa só parte dos comentários executáveis ----

    [Theory]
    // MySQL 8 executa /*!50000 e ignora /*!99999: roda DELETE FROM m i (as duas leituras modeladas dizem i).
    [InlineData("DELETE FROM /*!99999 i */ /*!50000 m */ i")]
    // MariaDB 11 executa /*!50000 e ignora /*M!999999.
    [InlineData("DELETE FROM /*M!999999 i */ /*!50000 m */ i")]
    [InlineData("UPDATE /*!99999 i */ /*!50000 m */ i SET v = 5")]
    [InlineData("DELETE FROM /*!99999 i */ /*! m */ i")]
    // Sentido inverso: a leitura principal acusa i, mas o servidor roda um DELETE filtrado em m.
    [InlineData("DELETE FROM /*!99999 i WHERE 1=1 OR id IN (SELECT 1 FROM */ m WHERE id=1 /*!99999 ) */ LIMIT 5")]
    public void Comentarios_executaveis_executados_em_parte_tornam_o_trecho_nao_analisavel(string sql) =>
        Assert.Contains(A.Analyze(sql).Dangers, d => d.Kind == DangerKind.Unanalyzable && d.Start == 0);

    // ---- Rodada 2 (Ruling I): comando que grava ou muda a estrutura com comentário executável é sempre não analisável ----

    [Theory]
    // Nenhuma leitura modelada tem perigo, mas o servidor roda "id=1 OR 1" (MySQL: /*!99999 ignorado; MariaDB: /*M!999999 ignorado).
    [InlineData("DELETE FROM m WHERE id=1 /*!50000 OR 1 */ /*!99999 =id */ LIMIT 5", "DELETE")]
    [InlineData("DELETE FROM m WHERE id=1 /*!50000 OR 1 */ /*M!999999 =id */ LIMIT 5", "DELETE")]
    [InlineData("UPDATE m SET v=9 WHERE id=1 /*!50000 OR 1 */ /*!99999 =id */ LIMIT 5", "UPDATE")]
    [InlineData("UPDATE m SET v=9 WHERE id=1 /*!50000 OR 1 */ /*M!999999 =id */ LIMIT 5", "UPDATE")]
    [InlineData("SELECT 1; DELETE FROM m WHERE id=1 /*!50000 OR 1 */ /*!99999 =id */ LIMIT 5", "DELETE")]
    [InlineData("SELECT 1; DELETE FROM m WHERE id=1 /*!50000 OR 1 */ /*M!999999 =id */ LIMIT 5", "DELETE")]
    // /*+ nunca roda como SQL: o servidor roda DELETE FROM m i.
    [InlineData("DELETE FROM /*+ i */ /*!50000 m */ i", "DELETE")]
    // O MariaDB lê /*m! (minúsculo) como comentário comum e /*M! como código.
    [InlineData("DELETE FROM /*m!100000 i */ /*M!100000 m */ i", "DELETE")]
    // O MySQL 8.4 lê só 5 dígitos de versão quando não vem espaço depois: "1x" é código.
    [InlineData("DELETE FROM /*!500001x */ x", "DELETE")]
    // Falsos positivos aceitos: comentário inofensivo num comando que grava.
    [InlineData("DELETE FROM i /*!40101 */", "DELETE")]
    [InlineData("DELETE FROM t /*!40101 LIMIT 1 */", "DELETE")]
    [InlineData("DELETE FROM /*!99999 m */ m", "DELETE")]
    [InlineData("DELETE /*!40101 LOW_PRIORITY */ FROM t", "DELETE")]
    [InlineData("DROP TABLE /*!32312 IF EXISTS */ t", "DROP")]
    [InlineData("UPDATE /*!50000 t */ SET a = 1 /*!99999 , b = 2 */ WHERE id = 1", "UPDATE")]
    [InlineData("INSERT INTO t VALUES (1) /*!50000 , (2) */", "INSERT")]
    [InlineData("WITH c AS (SELECT 1) DELETE FROM t WHERE id = 1 /*!50000 OR 1 */", "WITH")]
    [InlineData("SET STATEMENT max_statement_time=1 FOR DELETE FROM t WHERE id = 1 /*!50000 OR 1 */", "SET")]
    [InlineData("/*!99999 SELECT 1, */ DELETE FROM m WHERE id=1 /*!50000 OR 1 */", "DELETE")]
    public void Comando_que_grava_com_comentario_executavel_e_nao_analisavel(string sql, string command)
    {
        var at = sql.IndexOf(command, StringComparison.Ordinal);
        Assert.Contains(A.Analyze(sql).Dangers, d => d.Kind == DangerKind.Unanalyzable && d.Start <= at && at < d.Start + d.Length);
        Assert.False(A.IsReadOnly(sql, out _));
    }

    [Fact]
    public void Comentario_executavel_proprio_marca_so_o_DELETE_e_nao_o_SET_do_dump_que_vem_depois()
    {
        const string sql = "DELETE FROM t /*!40101 LIMIT 1 */;\n/*!50503 SET character_set_client = utf8mb4 */;";
        var a = A.Analyze(sql);
        var set = sql.IndexOf("SET", StringComparison.Ordinal);
        Assert.Equal([DangerKind.DeleteWithoutWhere, DangerKind.Unanalyzable], a.Dangers.Select(d => d.Kind).ToArray());
        Assert.All(a.Dangers, d => Assert.True(d.Start + d.Length <= set));
    }

    [Theory]
    [InlineData("/*!40101 SET NAMES utf8 */;\n/*!40103 SET TIME_ZONE='+00:00' */;\n/*!40014 SET FOREIGN_KEY_CHECKS=0 */;\nDELETE FROM t", DangerKind.DeleteWithoutWhere)]
    [InlineData("/*!40101 SET NAMES utf8 */;\nDELETE FROM t;\n/*!40014 SET FOREIGN_KEY_CHECKS=1 */;", DangerKind.DeleteWithoutWhere)]
    [InlineData("/*!40101 SET NAMES utf8 */; /*!50001 DROP TABLE t */", DangerKind.Drop)]
    [InlineData("/*! DELETE FROM t */", DangerKind.DeleteWithoutWhere)]
    public void Statement_sem_comentario_executavel_proprio_continua_como_antes(string sql, DangerKind kind) =>
        Assert.Equal([kind], Kinds(sql));

    [Theory]
    [InlineData("SELECT /*!50000 1 */, /*!99999 2 */")]
    [InlineData("/*!40101 SET NAMES utf8 */; /*!40103 SET TIME_ZONE='+00:00' */; DELETE FROM t WHERE id = 1")]
    [InlineData("DELETE FROM t WHERE id=1")]
    [InlineData("SELECT /*!40001 SQL_NO_CACHE */ * FROM t")]
    public void Sem_comando_que_grava_com_comentario_executavel_nada_muda(string sql) => Assert.Empty(Kinds(sql));

    [Theory]
    [InlineData("SELECT /*!40001 SQL_NO_CACHE */ * FROM t", true)]
    [InlineData("SELECT /*! STRAIGHT_JOIN */ a FROM t", true)]
    [InlineData("SELECT /*+ BKA(t) */ * FROM t", true)]
    // O servidor que executa só /*!50000 roda o DELETE (nas duas leituras modeladas tudo é SELECT).
    [InlineData("SELECT 1 /*!99999 , ' */ /*!50000 ; DELETE FROM m; -- ' */", false)]
    [InlineData("DELETE FROM m WHERE id=1 /*!50000 OR 1 */ /*!99999 =id */ LIMIT 5", false)]
    public void Somente_leitura_com_comentarios_executaveis(string sql, bool expected) =>
        Assert.Equal(expected, A.IsReadOnly(sql, out _));

    [Theory]
    [InlineData("DELETE /*+ BKA(i) */ FROM i WHERE id=1", null)]
    [InlineData("DELETE /*+ BKA(i) */ FROM i", "i")]
    [InlineData("UPDATE /*+ NO_MERGE() */ t SET a = 1", "t")]
    [InlineData("UPDATE /*+ BKA(a, b) */ t SET a = 1", "t")]
    public void Dica_de_otimizador_e_comentario_comum(string sql, string? target)
    {
        var dangers = A.Analyze(sql).Dangers;
        if (target is null) { Assert.Empty(dangers); return; }
        var d = Assert.Single(dangers);
        Assert.Equal(target, d.Target);
        Assert.Equal([$"SELECT COUNT(*) FROM {target}"], d.CountQueries);
    }

    [Theory]
    [InlineData("DELETE FROM t /*!40101 LIMIT 1 */", "DELETE FROM t /*!40101 LIMIT 1 */")]
    [InlineData("SELECT 1 /*! , 2 */;\nSELECT 3", "SELECT 1 /*! , 2 */")]
    [InlineData("/*!50001 DROP TABLE t */", "DROP TABLE t")]
    public void Batch_que_termina_dentro_de_comentario_executavel_leva_o_fechamento(string sql, string first) =>
        Assert.Equal(first, A.Analyze(sql).Batches[0].Text);

    [Fact]
    public void Analisador_do_SQL_Server_nunca_acusa_commit_implicito() =>
        Assert.False(((ISqlAnalyzer)SqlServerAnalyzer.Instance).CausesImplicitCommit("CREATE TABLE x (id INT); COMMIT"));
}
