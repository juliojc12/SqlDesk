using SqlDesk.Core.Connections;
using SqlDesk.Core.Providers;

namespace SqlDesk.Core.Tests;

public class ProviderTests
{
    [Fact]
    public void Registro_resolve_pelo_id_e_pela_conexao()
    {
        Assert.Equal(ProviderIds.SqlServer, ProviderRegistry.Get(ProviderIds.SqlServer).Id);
        Assert.Equal(ProviderIds.SqlServer, ProviderRegistry.For(new ConnectionSettings("s", "d", "u")).Id);
        Assert.Throws<ConnectionValidationException>(() => ProviderRegistry.Get("oracle"));
    }

    [Fact]
    public void SqlServer_expoe_o_sql_de_transacao_atual()
    {
        var p = ProviderRegistry.Get(ProviderIds.SqlServer);
        Assert.Equal("SELECT @@TRANCOUNT", p.Transactions.OpenCountSql);
        Assert.Equal("BEGIN TRANSACTION", p.Transactions.BeginSql);
        Assert.Equal("WHILE @@TRANCOUNT > 0 COMMIT TRANSACTION", p.Transactions.CommitAllSql);
        Assert.Equal("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", p.Transactions.RollbackAllSql);
        Assert.Equal("SAVE TRANSACTION sp1", p.Transactions.SavepointSql("sp1"));
        Assert.Equal("ROLLBACK TRANSACTION sp1", p.Transactions.RollbackToSavepointSql("sp1"));
        Assert.False(p.DdlCommitsImplicitly);
    }

    [Fact]
    public void SqlServer_traduz_login_invalido()
    {
        var p = ProviderRegistry.Get(ProviderIds.SqlServer);
        var settings = new ConnectionSettings("127.0.0.1,1", "d", "u", ConnectTimeout: 2);
        Assert.Equal(ProviderIds.SqlServer, p.Id);
        Assert.Contains("1433", p.DefaultPort.ToString());
        Assert.NotNull(p.BuildConnectionString(settings, "x"));
    }

    [Fact]
    public void SqlServer_traduz_excecao_generica_sem_alterar_a_mensagem()
    {
        var p = ProviderRegistry.Get(ProviderIds.SqlServer);
        var e = new FakeDbException("falhou");
        var f = p.Translate(e);
        Assert.Equal("falhou", f.Message);
        Assert.False(f.CertificateUntrusted);
        Assert.Null(p.ErrorNumber(e));
        Assert.Empty(p.ErrorDetails(e, 1));
    }

    private sealed class FakeDbException(string message) : System.Data.Common.DbException(message);
}
