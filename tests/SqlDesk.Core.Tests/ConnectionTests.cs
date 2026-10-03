using SqlDesk.Core.Connections;
using SqlDesk.Core.Providers;

namespace SqlDesk.Core.Tests;

public class ConnectionStringServiceTests
{
    [Fact]
    public void Parse_preenche_campos_e_preserva_chaves_desconhecidas()
    {
        var (s, pwd) = ConnectionStringService.Parse(
            "Server=srv,1433;Database=Vendas;User Id=app;Password=\"p@;ss\";Connect Timeout=5;Encrypt=false;TrustServerCertificate=true;Application Name=Foo;MultipleActiveResultSets=true");
        Assert.Equal("srv,1433", s.Server);
        Assert.Equal("Vendas", s.Database);
        Assert.Equal("app", s.User);
        Assert.Equal(5, s.ConnectTimeout);
        Assert.False(s.Encrypt);
        Assert.True(s.TrustServerCertificate);
        Assert.Equal("Foo", s.Advanced["Application Name"]);
        Assert.Equal("true", s.Advanced["Multiple Active Result Sets"]);
        Assert.NotNull(pwd);
    }

    [Fact]
    public void Build_e_Parse_fazem_ida_e_volta()
    {
        var original = new ConnectionSettings("srv", "db", "u", 20, 45, true, true,
            new Dictionary<string, string> { ["Application Name"] = "X" });
        var (back, pwd) = ConnectionStringService.Parse(ConnectionStringService.Build(original, "se;nha"));
        Assert.Equal("srv", back.Server);
        Assert.Equal("db", back.Database);
        Assert.Equal(20, back.ConnectTimeout);
        Assert.True(back.TrustServerCertificate);
        Assert.Equal("X", back.Advanced["Application Name"]);
        Assert.Equal("se;nha", pwd);
    }

    [Fact]
    public void Build_sem_senha_omite_a_chave()
    {
        var cs = ConnectionStringService.Build(new ConnectionSettings("srv", "db", "u"), null);
        Assert.DoesNotContain("Password", cs, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Server=a;Integrated Security=true")]
    [InlineData("Server=a;Trusted_Connection=yes")]
    [InlineData("Server=a;Authentication=Active Directory Default")]
    public void Parse_rejeita_autenticacao_fora_do_escopo(string cs) =>
        Assert.Throws<ConnectionValidationException>(() => ConnectionStringService.Parse(cs));

    [Fact]
    public void Parse_normaliza_nome_das_chaves_avancadas_e_rejeita_desconhecidas()
    {
        var (s, _) = ConnectionStringService.Parse("server=a;application name=Foo");
        Assert.Equal("Foo", s.Advanced["Application Name"]);
        Assert.Throws<ConnectionValidationException>(() => ConnectionStringService.Parse("server=a;chave inventada=1"));
    }

    [Fact]
    public void Parse_rejeita_string_malformada() =>
        Assert.Throws<ConnectionValidationException>(() => ConnectionStringService.Parse("Server=a;;=b;'"));
}

public class ConnectionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqldesk-tests-" + Guid.NewGuid());
    private string FilePath => Path.Combine(_dir, "connections.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private sealed class ReversingProtector : IPasswordProtector
    {
        public string Protect(string plain) => "P:" + new string(plain.Reverse().ToArray());
        public string Unprotect(string v) => new string(v[2..].Reverse().ToArray());
    }

    private ConnectionStore NewStore(IPasswordProtector? p = null) => new(FilePath, p ?? new ReversingProtector());

    private static SaveConnectionRequest Req(Guid? id = null, string? pwd = "segredo", string name = "Prod") =>
        new(id, name, "#FF0000", new ConnectionSettings("srv", "db", "u"), pwd);

    [Fact]
    public void Senha_nunca_e_gravada_em_texto_puro_e_persiste_entre_instancias()
    {
        var saved = NewStore().Save(Req());
        Assert.True(saved.HasPassword);
        Assert.DoesNotContain("segredo", File.ReadAllText(FilePath));

        var reopened = NewStore();
        Assert.Single(reopened.List());
        Assert.Equal("segredo", reopened.GetPassword(saved.Id));
    }

    [Fact]
    public void Editar_sem_senha_mantem_a_senha_salva()
    {
        var store = NewStore();
        var id = store.Save(Req()).Id;
        store.Save(Req(id, pwd: null, name: "Renomeada"));
        Assert.Equal("segredo", store.GetPassword(id));
        Assert.Equal("Renomeada", store.Get(id)!.Name);
    }

    [Fact]
    public void Duplicar_e_excluir()
    {
        var store = NewStore();
        var id = store.Save(Req()).Id;
        var copy = store.Duplicate(id);
        Assert.NotEqual(id, copy.Id);
        Assert.Equal("segredo", store.GetPassword(copy.Id));
        Assert.Equal(2, store.List().Count);
        Assert.True(store.Delete(id));
        Assert.Single(store.List());
        Assert.False(store.Delete(id));
    }

    [Theory]
    [InlineData("", "#FF0000")]
    [InlineData("ok", "vermelho")]
    [InlineData("ok", "#FFF")]
    public void Validacao_rejeita_dados_invalidos(string name, string color) =>
        Assert.Throws<ConnectionValidationException>(() => NewStore().Save(Req() with { Name = name, Color = color }));

    [Fact]
    public void Arquivo_corrompido_nao_e_sobrescrito()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ isto nao e json");
        var store = NewStore();
        Assert.Throws<InvalidDataException>(() => store.Save(Req()));
        Assert.Equal("{ isto nao e json", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Dpapi_faz_ida_e_volta()
    {
        var p = new DpapiPasswordProtector();
        var enc = p.Protect("s3nh@ çãõ");
        Assert.DoesNotContain("s3nh", enc);
        Assert.Equal("s3nh@ çãõ", p.Unprotect(enc));
    }
}

public class SqlErrorTranslatorTests
{
    [Theory]
    [InlineData(-2146893019, "qualquer")]
    [InlineData(0, "A cadeia de certificação foi emitida por uma autoridade que não é de confiança.")]
    [InlineData(-1, "The certificate chain was issued by an authority that is not trusted.")]
    public void Falha_de_certificado_vira_orientacao_sobre_TrustServerCertificate(int number, string message) =>
        Assert.Contains("TrustServerCertificate", SqlErrorTranslator.Classify(number, message));

    [Theory]
    [InlineData(-2146893019, "x", true)]
    [InlineData(0, "A cadeia de certificação foi emitida por uma autoridade que não é de confiança.", true)]
    [InlineData(18456, "Login failed for user 'x'.", false)]
    public void IsCertificate_distingue_falha_de_certificado_de_outras(int number, string message, bool expected) =>
        Assert.Equal(expected, SqlErrorTranslator.IsCertificate(number, message));

    [Fact]
    public void Login_invalido_e_traduzido() =>
        Assert.Contains("usuário ou senha", SqlErrorTranslator.Classify(18456, "Login failed for user 'x'."));

    [Fact]
    public void Erro_desconhecido_nao_e_classificado() =>
        Assert.Null(SqlErrorTranslator.Classify(9999, "outra coisa"));
}

public class ConnectionProviderFieldTests
{
    [Fact]
    public void Json_antigo_sem_provider_vira_sqlserver()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "connections.json");
        var id = Guid.NewGuid();
        var json = "{\"version\":1,\"connections\":[{\"id\":\"" + id + "\",\"name\":\"Velha\",\"color\":\"#0078D4\"," +
                   "\n \"settings\":{\"server\":\"srv\",\"database\":\"db\",\"user\":\"u\",\"connectTimeout\":15,\"commandTimeout\":30," +
                   "\n             \"encrypt\":true,\"trustServerCertificate\":false,\"advanced\":{}}}]}";
        File.WriteAllText(path, json);
        var store = new ConnectionStore(path, new NoopProtector());
        Assert.Equal(ProviderIds.SqlServer, store.Get(id)!.Settings.Provider);
    }

    [Fact]
    public void Provider_mysql_sobrevive_ao_salvar_e_ler()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "c.json");
        var store = new ConnectionStore(path, new NoopProtector());
        var saved = store.Save(new SaveConnectionRequest(null, "My", "#112233",
            new ConnectionSettings("h", "d", "u") { Provider = ProviderIds.MySql }, null));
        Assert.Equal(ProviderIds.MySql, new ConnectionStore(path, new NoopProtector()).Get(saved.Id)!.Settings.Provider);
    }

    private sealed class NoopProtector : IPasswordProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedText) => protectedText;
    }
}
