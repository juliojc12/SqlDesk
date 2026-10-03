using SqlDesk.Core.Execution;

namespace SqlDesk.Integration.Tests;

/// <summary>Travas (dupla confirmação) contra MySQL e MariaDB de verdade.</summary>
public class GuardTests
{
    /// <summary>Nome único por teste e servidor, para não colidir com outras execuções.</summary>
    private static string Table(string prefix, string server) => $"{prefix}_{server}_{Guid.NewGuid().ToString("N")[..8]}";

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Delete_sem_where_faz_rollback_de_verdade(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var t = Table("g1", server);
        try
        {
            await h.RunAsync($"CREATE TABLE {t} (id INT PRIMARY KEY) ENGINE=InnoDB; INSERT INTO {t} VALUES (1),(2),(3);");

            var outcome = await h.RunGuardedAsync($"DELETE FROM {t}");
            Assert.Equal(GuardStatus.PendingDecision, outcome.Status);
            var change = outcome.Pending!.Changes.Single();
            Assert.Equal(3, change.AffectedRows);
            Assert.False(change.HasPreview);
            Assert.False(outcome.Pending.PreviewUnavailable);
            Assert.False(outcome.Pending.Approximate);
            Assert.Equal(0L, await h.ScalarAsync($"SELECT COUNT(*) FROM {t}")); // apagado, mas ainda na transação

            await h.Guard.ResolveAsync(h.TabId, outcome.Pending.GuardId, commit: false);
            Assert.Equal(3L, await h.ScalarAsync($"SELECT COUNT(*) FROM {t}"));
            Assert.Equal(0, await h.Db.TranCountAsync(h.TabId, default));
        }
        finally
        {
            await h.RunAsync($"DROP TABLE IF EXISTS {t}");
        }
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Update_sem_where_commit_grava_e_rollback_desfaz(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var t = Table("gu", server);
        try
        {
            await h.RunAsync($"CREATE TABLE {t} (id INT PRIMARY KEY, v INT) ENGINE=InnoDB; INSERT INTO {t} VALUES (1,0),(2,0),(3,0);");

            var rolled = await h.RunGuardedAsync($"UPDATE {t} SET v = 9");
            Assert.Equal(GuardStatus.PendingDecision, rolled.Status);
            Assert.Equal(3, rolled.Pending!.Changes.Single().AffectedRows);
            await h.Guard.ResolveAsync(h.TabId, rolled.Pending.GuardId, commit: false);
            Assert.Equal(0L, await h.ScalarAsync($"SELECT COUNT(*) FROM {t} WHERE v = 9"));

            var committed = await h.RunGuardedAsync($"UPDATE {t} SET v = 7");
            Assert.Equal(GuardStatus.PendingDecision, committed.Status);
            await h.Guard.ResolveAsync(h.TabId, committed.Pending!.GuardId, commit: true);
            Assert.Equal(0, await h.Db.TranCountAsync(h.TabId, default));
            Assert.Equal(3L, await h.ScalarAsync($"SELECT COUNT(*) FROM {t} WHERE v = 7"));
        }
        finally
        {
            await h.RunAsync($"DROP TABLE IF EXISTS {t}");
        }
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Drop_executa_sem_segunda_confirmacao(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var t = Table("g2", server);
        try
        {
            await h.RunAsync($"CREATE TABLE {t} (id INT) ENGINE=InnoDB");

            var outcome = await h.RunGuardedAsync($"DROP TABLE {t}");

            Assert.Equal(GuardStatus.Completed, outcome.Status);
            Assert.Null(outcome.Pending);
            Assert.False(h.Guard.HasPending(h.TabId));
            Assert.Equal(0L, await h.ScalarAsync(
                $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = '{t}'"));
        }
        finally
        {
            await h.RunAsync($"DROP TABLE IF EXISTS {t}");
        }
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task DDL_com_transacao_aberta_avisa_e_o_commit_implicito_a_confirma(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var keep = Table("gk", server);
        var gone = Table("gt", server);
        try
        {
            await h.RunAsync($"CREATE TABLE {keep} (id INT PRIMARY KEY) ENGINE=InnoDB; CREATE TABLE {gone} (id INT) ENGINE=InnoDB;");
            await h.Db.ExecAsync(h.TabId, "START TRANSACTION", default);
            await h.Db.ExecAsync(h.TabId, $"INSERT INTO {keep} VALUES (1)", default);

            var outcome = await h.RunGuardedAsync($"TRUNCATE TABLE {gone}");

            Assert.Equal(GuardStatus.Completed, outcome.Status);
            Assert.Contains(h.LastSink.Messages, m => m.Text.Contains("transação que estava aberta"));
            // O TRUNCATE confirmou a transação da aba: não há mais o que desfazer.
            Assert.Equal(0, await h.Db.TranCountAsync(h.TabId, default));
            await h.Db.ExecAsync(h.TabId, "ROLLBACK", default);
            Assert.Equal(1L, await h.ScalarAsync($"SELECT COUNT(*) FROM {keep}"));
        }
        finally
        {
            await h.RunAsync($"DROP TABLE IF EXISTS {keep}; DROP TABLE IF EXISTS {gone};");
        }
    }

    /// <summary>
    /// Scripts com DELETE sem WHERE e um comando que confirma sozinho no MySQL/MariaDB. {t} = tabela com 3 linhas,
    /// {x} = nome livre para outra tabela. Achados da revisão de segurança (todos davam pending_decision com rollback falso
    /// ou tran_lost depois de prometer "nada é gravado").
    /// </summary>
    private static readonly Dictionary<string, (string Sql, bool OpenTransaction)> ImplicitCommitShapes = new()
    {
        ["delete_start"] = ("DELETE FROM {t}; START TRANSACTION;", false),
        ["delete_begin"] = ("DELETE FROM {t}; BEGIN;", false),
        ["delete_create_begin"] = ("DELETE FROM {t}; CREATE TABLE {x} (id INT) ENGINE=InnoDB; BEGIN;", false),
        ["delete_commit_start"] = ("DELETE FROM {t}; COMMIT; START TRANSACTION;", false),
        ["autocommit0_delete_create"] = ("SET autocommit=0; DELETE FROM {t}; CREATE TABLE {x} (id INT) ENGINE=InnoDB;", false),
        ["open_tran_delete_start"] = ("DELETE FROM {t}; START TRANSACTION;", true),
        ["delete_create"] = ("DELETE FROM {t}; CREATE TABLE {x} (id INT) ENGINE=InnoDB;", false),
        ["delete_create_index"] = ("DELETE FROM {t}; CREATE INDEX ix_{x} ON {t}(id);", false),
        ["delete_rename"] = ("DELETE FROM {t}; CREATE TABLE {x} (id INT) ENGINE=InnoDB; RENAME TABLE {x} TO {x}_r; RENAME TABLE {x}_r TO {x};", false),
        ["delete_lock"] = ("DELETE FROM {t}; LOCK TABLES {t} WRITE; UNLOCK TABLES;", false),
        ["delete_analyze"] = ("DELETE FROM {t}; ANALYZE TABLE {t};", false),
        ["create_delete"] = ("CREATE TABLE {x} (id INT) ENGINE=InnoDB; DELETE FROM {t};", false),
    };

    /// <summary>Os formatos em que a transação da trava some sem o @@in_transaction cair (o script reabre outra).</summary>
    private static readonly string[] ReopeningShapes =
        ["delete_start", "delete_begin", "delete_create_begin", "delete_commit_start", "autocommit0_delete_create", "open_tran_delete_start", "delete_create"];

    public static IEnumerable<object[]> ShapesPerServer() =>
        TestServers.All.SelectMany(s => ImplicitCommitShapes.Keys.Select(k => new object[] { s[0], k }));

    public static IEnumerable<object[]> ReopeningPerServer() =>
        TestServers.All.SelectMany(s => ReopeningShapes.Select(k => new object[] { s[0], k }));

    /// <summary>Cria a tabela com 3 linhas, roda o formato pela trava e devolve o resultado e as linhas que sobraram.</summary>
    private static async Task<(GuardOutcome Outcome, bool Irreversible, long Rows)> RunShapeAsync(
        Harness h, string shape, string t, string x)
    {
        var (template, openTransaction) = ImplicitCommitShapes[shape];
        var sql = template.Replace("{t}", t).Replace("{x}", x);
        await h.RunAsync($"CREATE TABLE {t} (id INT PRIMARY KEY) ENGINE=InnoDB; INSERT INTO {t} VALUES (1),(2),(3);");
        if (openTransaction) await h.Db.ExecAsync(h.TabId, "START TRANSACTION", default);

        var plan = Assert.IsType<ExecutionPlan.Dangerous>(
            ExecutionPlanner.Plan(h.Sessions.GetProvider(h.TabId)!.Analyzer, sql, 0, 0, 0, wholeScript: true));
        var irreversible = GuardedRunner.IsIrreversible(h.Sessions.GetProvider(h.TabId)!, plan);
        var outcome = await h.RunGuardedAsync(sql);

        // Se mesmo assim houvesse decisão pendente, o rollback teria de restaurar as 3 linhas.
        if (outcome.Pending is { } p) await h.Guard.ResolveAsync(h.TabId, p.GuardId, commit: false);
        await h.Db.ExecAsync(h.TabId, "ROLLBACK", default); // desfaz o que tiver ficado aberto; não pode trazer nada de volta
        return (outcome, irreversible, await h.ScalarAsync($"SELECT COUNT(*) FROM {t}"));
    }

    private static async Task CleanupAsync(Harness h, string t, string x)
    {
        try { await h.Db.ExecAsync(h.TabId, "ROLLBACK", default); } catch { /* conexão em qualquer estado */ }
        try { await h.Db.ExecAsync(h.TabId, "UNLOCK TABLES", default); } catch { }
        try { await h.Db.ExecAsync(h.TabId, "SET autocommit=1", default); } catch { }
        await h.RunAsync($"DROP TABLE IF EXISTS {t}; DROP TABLE IF EXISTS {x}; DROP TABLE IF EXISTS {x}_r;");
    }

    [IntegrationTheory, MemberData(nameof(ShapesPerServer))]
    public async Task DML_com_commit_implicito_no_script_roda_direto_e_avisa_que_e_irreversivel(string server, string shape)
    {
        await using var h = await Harness.OpenAsync(server);
        var (t, x) = (Table("gs", server), Table("gx", server));
        try
        {
            var (outcome, irreversible, rows) = await RunShapeAsync(h, shape, t, x);

            Assert.True(irreversible); // a primeira confirmação já diz que não tem volta
            Assert.Equal(GuardStatus.Completed, outcome.Status);
            Assert.Null(outcome.Pending);
            Assert.False(h.Guard.HasPending(h.TabId));
            Assert.DoesNotContain(h.LastSink.Messages, m => m.Text.Contains("nada é gravado"));
            Assert.Equal(0L, rows); // o DELETE ficou gravado, como avisado
        }
        finally
        {
            await CleanupAsync(h, t, x);
        }
    }

    [IntegrationTheory, MemberData(nameof(ReopeningPerServer))]
    public async Task Marca_de_savepoint_pega_o_commit_que_a_analise_nao_viu(string server, string shape)
    {
        // A análise léxica é desligada de propósito: só a marca de savepoint pode perceber que a transação acabou.
        await using var h = await Harness.OpenAsync(server, blindToImplicitCommit: true);
        var (t, x) = (Table("gb", server), Table("gy", server));
        try
        {
            var (outcome, irreversible, rows) = await RunShapeAsync(h, shape, t, x);

            Assert.True(irreversible); // o provedor real acusa; quem ficou cego foi só a trava
            Assert.Equal(GuardStatus.TransactionLost, outcome.Status);
            Assert.Null(outcome.Pending);
            Assert.False(h.Guard.HasPending(h.TabId));
            Assert.Contains(h.LastSink.Messages, m => m.Kind == MessageKinds.Error && m.Text.Contains("já foram gravadas"));
            Assert.Equal(0L, rows);
        }
        finally
        {
            await CleanupAsync(h, t, x);
        }
    }

    [IntegrationFact]
    public async Task MariaDB_SET_STATEMENT_FOR_CREATE_e_irreversivel_e_roda_direto()
    {
        // SET STATEMENT ... FOR é sintaxe do MariaDB (no MySQL é erro de sintaxe): só roda no container mariadb.
        const string server = "mariadb";
        await using var h = await Harness.OpenAsync(server);
        var (t, x) = (Table("gss", server), Table("gsx", server));
        try
        {
            await h.RunAsync($"CREATE TABLE {t} (id INT PRIMARY KEY) ENGINE=InnoDB; INSERT INTO {t} VALUES (1),(2),(3);");
            var sql = $"DELETE FROM {t}; SET STATEMENT max_statement_time=100 FOR CREATE TABLE {x} (id INT) ENGINE=InnoDB;";
            var plan = Assert.IsType<ExecutionPlan.Dangerous>(
                ExecutionPlanner.Plan(h.Sessions.GetProvider(h.TabId)!.Analyzer, sql, 0, 0, 0, wholeScript: true));

            Assert.True(GuardedRunner.IsIrreversible(h.Sessions.GetProvider(h.TabId)!, plan)); // primeira confirmação
            var outcome = await h.RunGuardedAsync(sql);

            Assert.Equal(GuardStatus.Completed, outcome.Status);
            Assert.Null(outcome.Pending);
            Assert.Equal(0L, await h.ScalarAsync($"SELECT COUNT(*) FROM {t}"));
            Assert.Equal(1L, await h.ScalarAsync(
                $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = '{x}'"));
        }
        finally
        {
            await CleanupAsync(h, t, x);
        }
    }

    // ---- Revisão final: tabela não transacional (MyISAM) não volta com ROLLBACK ----

    private static ExecutionPlan.Dangerous DangerPlan(Harness h, string sql) =>
        Assert.IsType<ExecutionPlan.Dangerous>(ExecutionPlanner.Plan(h.Sessions.GetProvider(h.TabId)!.Analyzer, sql, 0, 0, 0, wholeScript: true));

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Delete_sem_where_em_MyISAM_e_irreversivel_na_primeira_confirmacao_e_roda_direto(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var t = Table("gmy", server);
        try
        {
            await h.RunAsync($"CREATE TABLE {t} (id INT PRIMARY KEY) ENGINE=MyISAM; INSERT INTO {t} VALUES (1),(2),(3);");
            var sql = $"DELETE FROM {t}";

            // Primeira confirmação (mesma decisão do ExecuteHandler): "não pode ser desfeito".
            Assert.True(await h.Guard.IsIrreversibleAsync(h.TabId, DangerPlan(h, sql), default));
            Assert.True(await h.Guard.IsIrreversibleAsync(h.TabId, DangerPlan(h, $"DELETE FROM `sqldesk_test`.`{t}`"), default));

            var outcome = await h.RunGuardedAsync(sql);

            Assert.Equal(GuardStatus.Completed, outcome.Status);
            Assert.Null(outcome.Pending);
            Assert.False(h.Guard.HasPending(h.TabId));
            Assert.Contains(h.LastSink.Messages, m => m.Text.Contains("MyISAM"));
            Assert.Equal(0L, await h.ScalarAsync($"SELECT COUNT(*) FROM {t}"));
            Assert.Equal(0, await h.Db.TranCountAsync(h.TabId, default));
        }
        finally
        {
            await h.RunAsync($"DROP TABLE IF EXISTS {t}");
        }
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Delete_sem_where_em_InnoDB_continua_com_trava_reversivel(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var t = Table("gin", server);
        try
        {
            await h.RunAsync($"CREATE TABLE {t} (id INT PRIMARY KEY) ENGINE=InnoDB; INSERT INTO {t} VALUES (1),(2);");
            Assert.False(await h.Guard.IsIrreversibleAsync(h.TabId, DangerPlan(h, $"DELETE FROM {t}"), default));
            Assert.True(await h.Guard.IsIrreversibleAsync(h.TabId, DangerPlan(h, $"DELETE FROM {t}_nao_existe"), default));

            var outcome = await h.RunGuardedAsync($"DELETE FROM {t}");
            Assert.Equal(GuardStatus.PendingDecision, outcome.Status);
            var r = await h.Guard.ResolveAsync(h.TabId, outcome.Pending!.GuardId, commit: false);
            Assert.Equal("Rollback feito: as alterações foram desfeitas.", r.Message);
            Assert.Equal(2L, await h.ScalarAsync($"SELECT COUNT(*) FROM {t}"));
        }
        finally
        {
            await h.RunAsync($"DROP TABLE IF EXISTS {t}");
        }
    }

    /// <summary>
    /// UPDATE/DELETE com mais de uma tabela: {i} = InnoDB, {m} = MyISAM, cada uma com as linhas 1 e 2 (v = 0). A escrita
    /// cai em {m}, que o ROLLBACK não desfaz.
    /// </summary>
    public static IEnumerable<object[]> MultiTablePerServer() =>
        TestServers.All.SelectMany(s => new[]
        {
            "DELETE {m} FROM {i} JOIN {m} ON {i}.id = {m}.id",
            "UPDATE {i}, {m} SET {m}.v = 7",
            "UPDATE {i} JOIN {m} ON {i}.id = {m}.id SET {m}.v = 7",
        }.Select(sql => new object[] { s[0], sql }));

    [IntegrationTheory, MemberData(nameof(MultiTablePerServer))]
    public async Task UPDATE_DELETE_multi_tabela_com_MyISAM_e_irreversivel_e_roda_direto(string server, string template)
    {
        await using var h = await Harness.OpenAsync(server);
        var (i, m) = (Table("gmi", server), Table("gmm", server));
        try
        {
            await h.RunAsync($"CREATE TABLE {i} (id INT PRIMARY KEY, v INT) ENGINE=InnoDB; INSERT INTO {i} VALUES (1,0),(2,0);" +
                             $"CREATE TABLE {m} (id INT PRIMARY KEY, v INT) ENGINE=MyISAM; INSERT INTO {m} VALUES (1,0),(2,0);");
            var sql = template.Replace("{i}", i).Replace("{m}", m);

            // Primeira confirmação (mesma decisão do ExecuteHandler): "não pode ser desfeito"; o InnoDB sozinho continua reversível.
            Assert.True(await h.Guard.IsIrreversibleAsync(h.TabId, DangerPlan(h, sql), default));
            Assert.False(await h.Guard.IsIrreversibleAsync(h.TabId, DangerPlan(h, $"DELETE FROM {i}"), default));

            var outcome = await h.RunGuardedAsync(sql);

            Assert.Equal(GuardStatus.Completed, outcome.Status);
            Assert.Null(outcome.Pending);
            Assert.False(h.Guard.HasPending(h.TabId));
            Assert.Contains(h.LastSink.Messages, x => x.Text.Contains("sem segunda confirmação"));
            Assert.Equal(0, await h.Db.TranCountAsync(h.TabId, default));
            Assert.Equal(0L, await h.ScalarAsync($"SELECT COUNT(*) FROM {m} WHERE v = 0")); // gravado em MyISAM, como avisado
            Assert.Equal(2L, await h.ScalarAsync($"SELECT COUNT(*) FROM {i}"));
        }
        finally
        {
            await h.RunAsync($"DROP TABLE IF EXISTS {i}; DROP TABLE IF EXISTS {m}");
        }
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task View_como_alvo_e_irreversivel(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var (t, v) = (Table("gvt", server), Table("gvv", server));
        try
        {
            await h.RunAsync($"CREATE TABLE {t} (id INT PRIMARY KEY) ENGINE=InnoDB; CREATE VIEW {v} AS SELECT id FROM {t};");
            Assert.True(await h.Guard.IsIrreversibleAsync(h.TabId, DangerPlan(h, $"DELETE FROM {v}"), default));
        }
        finally
        {
            await h.RunAsync($"DROP VIEW IF EXISTS {v}; DROP TABLE IF EXISTS {t}");
        }
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Rollback_que_nao_desfaz_MyISAM_avisa_em_vez_de_dizer_que_desfez(string server)
    {
        // O DELETE perigoso é em InnoDB (trava normal), mas o mesmo trecho grava em MyISAM num comando sem perigo:
        // a checagem de mecanismo não vê esse alvo; o aviso 1196 do servidor no ROLLBACK é a rede de segurança.
        await using var h = await Harness.OpenAsync(server);
        var (inno, my) = (Table("gbi", server), Table("gbm", server));
        try
        {
            await h.RunAsync($"CREATE TABLE {inno} (id INT PRIMARY KEY) ENGINE=InnoDB; INSERT INTO {inno} VALUES (1),(2);" +
                             $"CREATE TABLE {my} (id INT PRIMARY KEY) ENGINE=MyISAM; INSERT INTO {my} VALUES (1),(2);");

            var outcome = await h.RunGuardedAsync($"DELETE FROM {inno}; DELETE FROM {my} WHERE id = 1;");
            Assert.Equal(GuardStatus.PendingDecision, outcome.Status);

            var r = await h.Guard.ResolveAsync(h.TabId, outcome.Pending!.GuardId, commit: false);

            Assert.Contains("não transacionais", r.Message);
            Assert.DoesNotContain("as alterações foram desfeitas", r.Message);
            Assert.Equal(2L, await h.ScalarAsync($"SELECT COUNT(*) FROM {inno}")); // InnoDB voltou
            Assert.Equal(1L, await h.ScalarAsync($"SELECT COUNT(*) FROM {my}"));   // MyISAM não
        }
        finally
        {
            await h.RunAsync($"DROP TABLE IF EXISTS {inno}; DROP TABLE IF EXISTS {my}");
        }
    }
}
