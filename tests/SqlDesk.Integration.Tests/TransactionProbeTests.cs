using System.Data.Common;
using SqlDesk.Core.Connections;
using SqlDesk.Core.Execution;
using SqlDesk.Core.Providers;
using SqlDesk.Core.Sessions;

namespace SqlDesk.Integration.Tests;

public class TransactionProbeTests
{
    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Conta_transacao_aberta_e_fechada(string server)
    {
        var provider = ProviderRegistry.Get(ProviderIds.MySql);
        await using var conn = provider.CreateConnection(provider.BuildConnectionString(TestServers.For(server), TestServers.Password));
        await conn.OpenAsync();

        Assert.Equal(0, await MySqlTransactionProbe.OpenCountAsync(conn, default));
        await Exec(conn, "START TRANSACTION");
        Assert.Equal(1, await MySqlTransactionProbe.OpenCountAsync(conn, default));
        await Exec(conn, "ROLLBACK");
        Assert.Equal(0, await MySqlTransactionProbe.OpenCountAsync(conn, default));
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Ve_o_BEGIN_e_o_COMMIT_do_proprio_script(string server)
    {
        await using var conn = await OpenAsync(server);

        await Exec(conn, "BEGIN");
        Assert.Equal(1, await MySqlTransactionProbe.OpenCountAsync(conn, default));
        Assert.Equal(1, await MySqlTransactionProbe.OpenCountAsync(conn, default)); // consultar de novo não muda nada
        await Exec(conn, "COMMIT");
        Assert.Equal(0, await MySqlTransactionProbe.OpenCountAsync(conn, default));
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Consultar_nao_altera_o_trabalho_da_transacao_aberta(string server)
    {
        await using var conn = await OpenAsync(server);
        var table = "probe_" + Guid.NewGuid().ToString("N")[..8];
        await Exec(conn, $"CREATE TABLE {table} (id INT PRIMARY KEY) ENGINE=InnoDB");
        try
        {
            await Exec(conn, "START TRANSACTION");
            await Exec(conn, $"INSERT INTO {table} VALUES (1)");
            Assert.Equal(1, await MySqlTransactionProbe.OpenCountAsync(conn, default));
            await Exec(conn, $"INSERT INTO {table} VALUES (2)");
            Assert.Equal(2L, await Scalar(conn, $"SELECT COUNT(*) FROM {table}"));
            await Exec(conn, "ROLLBACK");

            // Tudo desfeito, inclusive o que veio antes da consulta: a sonda não confirmou nada por conta própria.
            Assert.Equal(0L, await Scalar(conn, $"SELECT COUNT(*) FROM {table}"));
            Assert.Equal(0, await MySqlTransactionProbe.OpenCountAsync(conn, default));
        }
        finally
        {
            await Exec(conn, $"DROP TABLE {table}");
        }
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Servico_de_transacao_na_aba_conta_pelo_servidor(string server)
    {
        await using var tab = await TabAsync(server);
        var db = new SessionDb(tab.Sessions);
        var svc = new TransactionService(db);

        Assert.Equal(0, await svc.RefreshAsync("t"));
        Assert.Equal(1, await svc.BeginAsync("t"));
        Assert.Equal(0, await svc.CommitAsync("t"));
        Assert.Equal(1, await svc.BeginAsync("t"));
        Assert.Equal(0, await svc.RollbackAsync("t"));

        // Transação aberta pelo próprio script (fora dos botões do app): a contagem vem do servidor.
        await db.ExecAsync("t", "START TRANSACTION", default);
        Assert.Equal(1, await svc.RefreshAsync("t"));
        await db.ExecAsync("t", "COMMIT", default);
        Assert.Equal(0, await svc.RefreshAsync("t"));
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Sem_como_consultar_usa_o_estado_rastreado_pelo_app(string server)
    {
        await using var tab = await TabAsync(server);
        var db = new SessionDb(tab.Sessions, probe: (_, _) => Task.FromResult<int?>(null));
        var svc = new TransactionService(db);

        Assert.Equal(1, await svc.BeginAsync("t"));
        Assert.Equal(1, await svc.RefreshAsync("t")); // nunca "0 por falta de informação"
        Assert.Equal(0, await svc.RollbackAsync("t"));
    }

    private sealed class Tab(TabSessionManager sessions, string dir) : IAsyncDisposable
    {
        public TabSessionManager Sessions { get; } = sessions;

        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync();
            try { Directory.Delete(dir, true); } catch { /* pasta temporária */ }
        }
    }

    private sealed class NoProtector : IPasswordProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    /// <summary>Aba "t" conectada ao servidor. A senha vai só em memória (não é salva no arquivo da conexão).</summary>
    private static async Task<Tab> TabAsync(string server)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqldesk-it-" + Guid.NewGuid().ToString("N"));
        var store = new ConnectionStore(Path.Combine(dir, "c.json"), new NoProtector());
        var id = store.Save(new SaveConnectionRequest(null, server, "#112233", TestServers.For(server), null)).Id;
        var sessions = new TabSessionManager(store);
        await sessions.OpenAsync("t", id, TestServers.Password);
        return new Tab(sessions, dir);
    }

    private static async Task<DbConnection> OpenAsync(string server)
    {
        var provider = ProviderRegistry.Get(ProviderIds.MySql);
        var conn = provider.CreateConnection(provider.BuildConnectionString(TestServers.For(server), TestServers.Password));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task Exec(DbConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(DbConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }
}
