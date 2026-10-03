using SqlDesk.Core.Metadata;
using SqlDesk.Core.Providers;

namespace SqlDesk.Integration.Tests;

public class MetadataIntegrationTests
{
    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Carrega_objetos_e_colunas_pelo_information_schema(string server)
    {
        var provider = ProviderRegistry.Get(ProviderIds.MySql);
        var settings = TestServers.For(server);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var table = "m1_" + suffix;
        var view = "v1_" + suffix;

        await using var setup = provider.CreateConnection(provider.BuildConnectionString(settings, TestServers.Password));
        await setup.OpenAsync();
        await Exec(setup, $"CREATE TABLE {table} (id INT, nome VARCHAR(50))");
        try
        {
            await Exec(setup, $"CREATE VIEW {view} AS SELECT id, nome FROM {table}");

            var id = Guid.NewGuid();
            var service = new MetadataService(async (_, _) =>
            {
                var conn = provider.CreateConnection(provider.BuildConnectionString(settings, TestServers.Password));
                await conn.OpenAsync();
                return (conn, provider);
            });
            var done = new TaskCompletionSource<string?>();
            Assert.True(service.StartLoad(id, true, (phase, msg) =>
            {
                if (phase == MetaPhase.Columns) done.TrySetResult(null);
                else if (phase == MetaPhase.Error) done.TrySetResult(msg ?? "erro");
            }));
            Assert.Null(await done.Task.WaitAsync(TimeSpan.FromSeconds(30)));

            var snap = service.Get(id)!;
            Assert.True(snap.ColumnsLoaded);
            Assert.False(snap.HasSchemaLevel);
            Assert.Contains(new MetaObject("sqldesk_test", table, "table"), snap.Objects);
            Assert.Contains(new MetaObject("sqldesk_test", view, "view"), snap.Objects);
            Assert.Contains("sqldesk_test", snap.Schemas);
            var cols = snap.Columns[$"sqldesk_test.{table}"];
            Assert.Equal(["id", "nome"], cols.Select(c => c.Name));
            // COLUMN_TYPE vem como o servidor o grava: o MariaDB 11 devolve "int(11)", o MySQL 8 devolve "int".
            Assert.Matches(@"^int(\(\d+\))?$", cols[0].Type);
            Assert.Equal("varchar(50)", cols[1].Type);
        }
        finally
        {
            await Exec(setup, $"DROP VIEW IF EXISTS {view}");
            await Exec(setup, $"DROP TABLE IF EXISTS {table}");
        }
    }

    private static async Task Exec(System.Data.Common.DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
