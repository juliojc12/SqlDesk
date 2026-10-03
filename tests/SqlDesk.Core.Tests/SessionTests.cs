using SqlDesk.Core.Connections;
using SqlDesk.Core.Sessions;

namespace SqlDesk.Core.Tests;

public class SessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqldesk-tests-" + Guid.NewGuid());

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private sealed class PlainProtector : IPasswordProtector
    {
        public string Protect(string plain) => "P:" + plain;
        public string Unprotect(string v) => v[2..];
    }

    private ConnectionStore NewStore() => new(Path.Combine(_dir, "c.json"), new PlainProtector());

    [Fact]
    public async Task Abrir_sem_senha_salva_exige_senha_e_nao_tenta_conectar()
    {
        var store = NewStore();
        var id = store.Save(new SaveConnectionRequest(null, "X", "#112233", new ConnectionSettings("127.0.0.1,1", "db", "u"), null)).Id;
        await using var mgr = new TabSessionManager(store);
        await Assert.ThrowsAsync<PasswordRequiredException>(() => mgr.OpenAsync("t1", id, null));
        Assert.False(mgr.IsConnected("t1"));
    }

    [Fact]
    public async Task Abrir_conexao_inexistente_falha_com_validacao()
    {
        await using var mgr = new TabSessionManager(NewStore());
        await Assert.ThrowsAsync<ConnectionValidationException>(() => mgr.OpenAsync("t1", Guid.NewGuid(), "x"));
    }

    [Fact]
    public async Task Falha_de_rede_vira_ConnectFailed_e_nao_deixa_sessao()
    {
        var store = NewStore();
        var id = store.Save(new SaveConnectionRequest(null, "X", "#112233",
            new ConnectionSettings("127.0.0.1,1", "db", "u", ConnectTimeout: 2), "senha")).Id;
        await using var mgr = new TabSessionManager(store);
        await Assert.ThrowsAsync<ConnectFailedException>(() => mgr.OpenAsync("t1", id, null));
        Assert.False(mgr.IsConnected("t1"));
    }

    [Fact]
    public async Task Contador_rastreado_de_transacao_por_aba_zera_ao_desconectar()
    {
        await using var mgr = new TabSessionManager(NewStore());
        Assert.Equal(0, mgr.TrackedTransactionCount("t1"));

        mgr.SetTrackedTransactions("t1", 1);
        Assert.Equal(1, mgr.TrackedTransactionCount("t1"));
        Assert.Equal(0, mgr.TrackedTransactionCount("t2"));

        await mgr.DisconnectAsync("t1");
        Assert.Equal(0, mgr.TrackedTransactionCount("t1"));
    }

    [Fact]
    public async Task Fechar_aba_inexistente_nao_falha()
    {
        await using var mgr = new TabSessionManager(NewStore());
        await mgr.DisconnectAsync("nada");
    }

    [Fact]
    public void Estado_das_abas_faz_ida_e_volta()
    {
        var s = new SessionStateStore(Path.Combine(_dir, "session.json"));
        Assert.Null(s.Load());
        s.Save("{\"tabs\":[{\"text\":\"SELECT 'ação'\"}]}");
        Assert.Equal("{\"tabs\":[{\"text\":\"SELECT 'ação'\"}]}", new SessionStateStore(Path.Combine(_dir, "session.json")).Load());
    }
}
