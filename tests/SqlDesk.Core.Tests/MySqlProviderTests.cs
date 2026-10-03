using SqlDesk.Core.Connections;
using SqlDesk.Core.Providers;

namespace SqlDesk.Core.Tests;

public class MySqlProviderTests
{
    private static readonly IDatabaseProvider P = ProviderRegistry.Get(ProviderIds.MySql);

    [Fact]
    public void Build_inclui_porta_e_ssl()
    {
        var cs = P.BuildConnectionString(
            new ConnectionSettings("db.exemplo.com:3307", "vendas", "app", 10, 45, Encrypt: true, TrustServerCertificate: true)
            { Provider = ProviderIds.MySql }, "se;nh=a'\"");
        var b = new MySqlConnector.MySqlConnectionStringBuilder(cs);
        Assert.Equal("db.exemplo.com", b.Server);
        Assert.Equal(3307u, b.Port);
        Assert.Equal("vendas", b.Database);
        Assert.Equal("app", b.UserID);
        Assert.Equal("se;nh=a'\"", b.Password);
        Assert.Equal(MySqlConnector.MySqlSslMode.Required, b.SslMode);
        Assert.Equal(10u, b.ConnectionTimeout);
    }

    [Fact]
    public void Build_liga_data_zero_por_padrao_para_a_grade_nao_estourar()
    {
        var cs = P.BuildConnectionString(new ConnectionSettings("h", "d", "u") { Provider = ProviderIds.MySql }, null);
        var b = new MySqlConnector.MySqlConnectionStringBuilder(cs);
        Assert.True(b.AllowZeroDateTime);
        Assert.False(b.ConvertZeroDateTime);
    }

    [Fact]
    public void Build_respeita_a_data_zero_escolhida_no_avancado()
    {
        var s = new ConnectionSettings("h", "d", "u") { Provider = ProviderIds.MySql, Advanced = new Dictionary<string, string> { ["AllowZeroDateTime"] = "false" } };
        Assert.False(new MySqlConnector.MySqlConnectionStringBuilder(P.BuildConnectionString(s, null)).AllowZeroDateTime);
    }

    [Theory]
    [InlineData(false, false, "None")]
    [InlineData(true, true, "Required")]
    [InlineData(true, false, "VerifyCA")]
    public void Mapeia_encrypt_e_trust_para_ssl(bool encrypt, bool trust, string mode)
    {
        var cs = P.BuildConnectionString(new ConnectionSettings("h", "d", "u", Encrypt: encrypt, TrustServerCertificate: trust) { Provider = ProviderIds.MySql }, null);
        Assert.Equal(mode, new MySqlConnector.MySqlConnectionStringBuilder(cs).SslMode.ToString());
    }

    [Fact]
    public void Parse_e_build_fazem_ida_e_volta()
    {
        var (s, pwd) = P.ParseConnectionString("Server=h;Port=3307;Database=d;User ID=u;Password=p;SslMode=VerifyFull;Connection Timeout=7;Default Command Timeout=50;Application Name=Foo");
        Assert.Equal("h:3307", s.Server);
        Assert.Equal(ProviderIds.MySql, s.Provider);
        Assert.True(s.Encrypt);
        Assert.False(s.TrustServerCertificate);
        Assert.Equal(7, s.ConnectTimeout);
        Assert.Equal(50, s.CommandTimeout);
        Assert.Equal("p", pwd);
        Assert.Equal("Foo", s.Advanced["ApplicationName"]);
    }

    [Fact]
    public void Parse_recusa_chave_desconhecida() =>
        Assert.Throws<ConnectionValidationException>(() => P.ParseConnectionString("Server=h;ChaveInexistente=1"));

    [Theory]
    [InlineData("host", "host", 3306u)]
    [InlineData("host:3307", "host", 3307u)]
    [InlineData("::1", "::1", 3306u)]
    [InlineData("[::1]", "::1", 3306u)]
    [InlineData("[::1]:3307", "::1", 3307u)]
    [InlineData("fe80::1", "fe80::1", 3306u)]
    public void Server_valido_vira_host_e_porta(string server, string host, uint port)
    {
        var (h, p) = MySqlProvider.ParseServer(server);
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("abc:xyz")]
    [InlineData("host:99999")]
    [InlineData("host:0")]
    [InlineData("[::1]:0")]
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(":3307")]
    public void Server_invalido_e_recusado(string server) =>
        Assert.Throws<ConnectionValidationException>(() => MySqlProvider.ParseServer(server));

    [Fact]
    public void Ipv6_faz_ida_e_volta()
    {
        var s = new ConnectionSettings("[::1]:3307", "d", "u") { Provider = ProviderIds.MySql };
        var cs = P.BuildConnectionString(s, "p");
        Assert.Equal("::1", new MySqlConnector.MySqlConnectionStringBuilder(cs).Server);
        var (parsed, pwd) = P.ParseConnectionString(cs);
        Assert.Equal("[::1]:3307", parsed.Server);
        Assert.Equal(cs, P.BuildConnectionString(parsed, pwd));

        var (padrao, _) = P.ParseConnectionString(P.BuildConnectionString(new ConnectionSettings("::1", "d", "u") { Provider = ProviderIds.MySql }, null));
        Assert.Equal("::1", padrao.Server);
    }

    [Fact]
    public void Acesso_negado() =>
        Assert.Contains("usuário ou senha", MySqlProvider.Classify(MySqlConnector.MySqlErrorCode.AccessDenied, "Access denied for user 'u'@'h' (using password: YES)", null).Message);

    [Fact]
    public void Banco_desconhecido_ou_sem_acesso()
    {
        Assert.Contains("não existe", MySqlProvider.Classify(MySqlConnector.MySqlErrorCode.UnknownDatabase, "Unknown database 'x'", null).Message);
        Assert.Contains("não existe", MySqlProvider.Classify(MySqlConnector.MySqlErrorCode.DatabaseAccessDenied, "Access denied for user 'u'@'%' to database 'x'", null).Message);
    }

    [Fact]
    public void Servidor_inalcancavel()
    {
        var f1 = MySqlProvider.Classify(MySqlConnector.MySqlErrorCode.UnableToConnectToHost, "Unable to connect to any of the specified MySQL hosts.", null);
        Assert.Contains("alcançar o servidor", f1.Message);
        Assert.False(f1.CertificateUntrusted);
        var f2 = MySqlProvider.Classify(null, "falhou", new System.Net.Sockets.SocketException());
        Assert.Contains("alcançar o servidor", f2.Message);
    }

    [Fact]
    public void Falha_de_tls_e_certificado_nao_confiavel()
    {
        // Forma real observada: UnableToConnectToHost + "SSL Authentication Error" + AuthenticationException interna.
        var inner = new System.Security.Authentication.AuthenticationException("The remote certificate was rejected due to the following error: RemoteCertificateChainErrors");
        var f = MySqlProvider.Classify(MySqlConnector.MySqlErrorCode.UnableToConnectToHost, "SSL Authentication Error", inner);
        Assert.True(f.CertificateUntrusted);
        Assert.Contains("Confiar no certificado", f.Message);
        Assert.True(MySqlProvider.Classify(null, "SSL Authentication Error", null).CertificateUntrusted);
        Assert.True(MySqlProvider.Classify(null, "x", inner).CertificateUntrusted);
    }

    [Fact]
    public void Erro_generico_repassa_a_mensagem_sem_segredos()
    {
        var f = MySqlProvider.Classify(MySqlConnector.MySqlErrorCode.ParseError, "You have an error in your SQL syntax", null);
        Assert.Equal("You have an error in your SQL syntax", f.Message);
        Assert.False(f.CertificateUntrusted);
        Assert.DoesNotContain("s3nh4-de-teste", MySqlProvider.Classify(MySqlConnector.MySqlErrorCode.AccessDenied, "Access denied", null).Message);
    }

    [Fact]
    public void Sql_de_transacao_do_mysql()
    {
        var t = P.Transactions;
        Assert.Equal("START TRANSACTION", t.BeginSql);
        Assert.Equal("COMMIT", t.CommitAllSql);
        Assert.Equal("ROLLBACK", t.RollbackAllSql);
        Assert.Equal("SAVEPOINT sp1", t.SavepointSql("sp1"));
        Assert.Equal("ROLLBACK TO SAVEPOINT sp1", t.RollbackToSavepointSql("sp1"));
        Assert.True(P.DdlCommitsImplicitly);
    }

    // ---- Revisão final: chaves avançadas fora do escopo (só usuário e senha, e SslMode pelas caixas) ----

    public static TheoryData<string, string> ChavesForaDoEscopo => new()
    {
        { "AllowLoadLocalInfile", "true" },
        { "Allow Load Local Infile", "true" },
        { "CertificateFile", "x.pfx" },
        { "Certificate File", "x.pfx" },
        { "CertificatePassword", "segredo" },
        { "Certificate Password", "segredo" },
        { "CertificateThumbprint", "abc" },
        { "CertificateStoreLocation", "CurrentUser" },
        { "SslCert", "c.pem" },
        { "Ssl-Cert", "c.pem" },
        { "SslKey", "k.pem" },
        { "Ssl-Key", "k.pem" },
        { "SslCa", "ca.pem" },
        { "CA Certificate File", "ca.pem" },
        { "ConnectionProtocol", "Pipe" },
        { "Protocol", "Unix" },
        { "Connection Protocol", "Memory" },
        { "PipeName", "MYSQL" },
        { "Pipe", "MYSQL" },
        { "ServerRsaPublicKeyFile", "rsa.pem" },
        { "ServerSPN", "mysql/host" },
    };

    [Theory, MemberData(nameof(ChavesForaDoEscopo))]
    public void Parse_recusa_chave_fora_do_escopo(string key, string value)
    {
        var ex = Assert.Throws<ConnectionValidationException>(() => P.ParseConnectionString($"Server=h;User ID=u;{key}={value}"));
        Assert.Contains("fora do escopo", ex.Message);
    }

    [Theory, MemberData(nameof(ChavesForaDoEscopo))]
    public void Build_recusa_chave_fora_do_escopo_em_arquivo_editado(string key, string value)
    {
        var s = new ConnectionSettings("h", "d", "u") { Provider = ProviderIds.MySql, Advanced = new Dictionary<string, string> { [key] = value } };
        var ex = Assert.Throws<ConnectionValidationException>(() => P.BuildConnectionString(s, "p"));
        Assert.Contains("fora do escopo", ex.Message);
    }

    [Theory, MemberData(nameof(ChavesForaDoEscopo))]
    public void Salvar_recusa_chave_fora_do_escopo(string key, string value)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "c.json");
        var store = new ConnectionStore(path, new NoopProtector());
        var s = new ConnectionSettings("h", "d", "u") { Provider = ProviderIds.MySql, Advanced = new Dictionary<string, string> { [key] = value } };
        Assert.Throws<ConnectionValidationException>(() => store.Save(new SaveConnectionRequest(null, "My", "#112233", s, null)));
        Assert.False(File.Exists(path) && File.ReadAllText(path).Contains(value));
    }

    [Theory]
    [InlineData("Password", "p")]
    [InlineData("pwd", "p")]
    [InlineData("SslMode", "None")]
    [InlineData("SSL Mode", "Required")]
    [InlineData("Server", "outro")]
    [InlineData("User ID", "root")]
    public void Build_recusa_chave_tratada_por_campo_proprio_em_Avancado(string key, string value)
    {
        var s = new ConnectionSettings("h", "d", "u") { Provider = ProviderIds.MySql, Advanced = new Dictionary<string, string> { [key] = value } };
        Assert.Throws<ConnectionValidationException>(() => P.BuildConnectionString(s, "p"));
    }

    [Theory]
    [InlineData("ApplicationName", "Foo")]
    [InlineData("Application Name", "Foo")]
    [InlineData("AllowPublicKeyRetrieval", "true")]
    [InlineData("Pooling", "false")]
    [InlineData("ConnectionProtocol", "Tcp")]
    [InlineData("AllowZeroDateTime", "false")]
    public void Chave_avancada_permitida_continua_passando(string key, string value)
    {
        var (parsed, _) = P.ParseConnectionString($"Server=h;User ID=u;{key}={value}");
        Assert.Single(parsed.Advanced);
        var s = new ConnectionSettings("h", "d", "u") { Provider = ProviderIds.MySql, Advanced = new Dictionary<string, string> { [key] = value } };
        Assert.NotEmpty(P.BuildConnectionString(s, "p"));
    }

    [Fact]
    public void Parse_continua_tirando_senha_e_ssl_para_os_campos()
    {
        var (s, pwd) = P.ParseConnectionString("Server=h;User ID=u;Password=p;SslMode=Required");
        Assert.Equal("p", pwd);
        Assert.Empty(s.Advanced);
        Assert.True(s.Encrypt);
    }

    private sealed class NoopProtector : IPasswordProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedText) => protectedText;
    }

    // ---- Revisão final: nome do alvo para a consulta de mecanismo (information_schema) ----

    [Theory]
    [InlineData("t", null, "t")]
    [InlineData("db.t", "db", "t")]
    [InlineData("`db`.`t`", "db", "t")]
    [InlineData("`we``ird`", null, "we`ird")]
    [InlineData("`a.b`.c", "a.b", "c")]
    [InlineData("db.`x y`", "db", "x y")]
    [InlineData("tabela_ção", null, "tabela_ção")]
    public void Separa_banco_e_tabela_do_alvo(string target, string? schema, string name)
    {
        Assert.True(MySqlProvider.TrySplitTableName(target, out var s, out var n));
        Assert.Equal(schema, s);
        Assert.Equal(name, n);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.b.c")]
    [InlineData("`aberto")]
    [InlineData("`a`b`")]
    [InlineData("db.")]
    [InlineData(".t")]
    [InlineData("'t'")]
    [InlineData("\"t\"")]
    [InlineData("``")]
    [InlineData("a b")]
    public void Alvo_malformado_nao_e_separado(string target) =>
        Assert.False(MySqlProvider.TrySplitTableName(target, out _, out _));

    [Theory]
    [InlineData("InnoDB", true)]
    [InlineData("innodb", true)]
    [InlineData("MyISAM", false)]
    [InlineData("Aria", false)]
    [InlineData("MEMORY", false)]
    [InlineData("CSV", false)]
    [InlineData(null, false)] // view
    public void So_InnoDB_conta_como_transacional(string? engine, bool expected) =>
        Assert.Equal(expected, P.Transactions.IsTransactionalEngine(engine));

    [Fact]
    public void Consulta_de_mecanismo_e_parametrizada()
    {
        var sql = P.Transactions.TableEngineSql!;
        Assert.Contains("@schema", sql);
        Assert.Contains("@name", sql);
        Assert.Contains("information_schema.TABLES", sql);
        Assert.Equal(1196, P.Transactions.RollbackIncompleteWarning);
        var ss = ProviderRegistry.Get(ProviderIds.SqlServer).Transactions;
        Assert.Null(ss.TableEngineSql);
        Assert.Null(ss.RollbackIncompleteWarning);
    }
}
