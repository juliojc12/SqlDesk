using System.Net;
using System.Text;
using SqlDesk.Core.Ai;
using SqlDesk.Core.Connections;
using SqlDesk.Core.Metadata;

namespace SqlDesk.Core.Tests;

public class AiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqldesk-ai-" + Guid.NewGuid());

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private sealed class PlainProtector : IPasswordProtector
    {
        public string Protect(string plain) => "P:" + plain;
        public string Unprotect(string v) => v[2..];
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static (AiClient Client, FakeHandler Handler) Client(HttpStatusCode status, string body)
    {
        var h = new FakeHandler(status, body);
        return (new AiClient(new HttpClient(h)), h);
    }

    private const string Inner = "{\\\"sql\\\":\\\"SELECT 1\\\",\\\"notes\\\":\\\"ok\\\"}";

    [Fact]
    public async Task OpenAi_monta_o_pedido_e_le_a_resposta()
    {
        var (c, h) = Client(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"" + Inner + "\"}}]}");
        var r = await c.GenerateAsync(AiProviders.OpenAi, "gpt-x", "KEY", "sys", "usr", default);
        Assert.Equal("SELECT 1", r.Sql);
        Assert.Equal("ok", r.Notes);
        Assert.Equal("https://api.openai.com/v1/chat/completions", h.Last!.RequestUri!.ToString());
        Assert.Equal("Bearer KEY", h.Last.Headers.GetValues("Authorization").Single());
        Assert.Contains("\"usr\"", h.LastBody);
    }

    [Fact]
    public async Task Anthropic_monta_o_pedido_e_le_a_resposta()
    {
        var (c, h) = Client(HttpStatusCode.OK, "{\"content\":[{\"type\":\"text\",\"text\":\"" + Inner + "\"}]}");
        var r = await c.GenerateAsync(AiProviders.Anthropic, "claude-x", "KEY", "sys", "usr", default);
        Assert.Equal("SELECT 1", r.Sql);
        Assert.Equal("KEY", h.Last!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("https://api.anthropic.com/v1/messages", h.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task Gemini_monta_o_pedido_e_le_a_resposta()
    {
        var (c, h) = Client(HttpStatusCode.OK, "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"" + Inner + "\"}]}}]}");
        var r = await c.GenerateAsync(AiProviders.Gemini, "gemini-x", "KEY", "sys", "usr", default);
        Assert.Equal("SELECT 1", r.Sql);
        Assert.Equal("KEY", h.Last!.Headers.GetValues("x-goog-api-key").Single());
        Assert.EndsWith("/models/gemini-x:generateContent", h.Last.RequestUri!.ToString());
        Assert.DoesNotContain("KEY", h.Last.RequestUri.ToString());
    }

    [Theory]
    [InlineData(401, "unauthorized")]
    [InlineData(403, "unauthorized")]
    [InlineData(404, "not_found")]
    [InlineData(429, "rate_limit")]
    [InlineData(500, "server")]
    public async Task Erros_http_viram_mensagens_sem_vazar_a_chave(int status, string code)
    {
        var (c, _) = Client((HttpStatusCode)status, "{\"error\":\"SEGREDO-DO-CORPO\"}");
        var ex = await Assert.ThrowsAsync<AiException>(() => c.GenerateAsync(AiProviders.OpenAi, "m", "KEY-123", "s", "u", default));
        Assert.Equal(code, ex.Code);
        Assert.DoesNotContain("KEY-123", ex.Message);
        Assert.DoesNotContain("SEGREDO", ex.Message);
    }

    [Fact]
    public async Task Resposta_invalida_vira_bad_response()
    {
        var (c, _) = Client(HttpStatusCode.OK, "isto não é json");
        var ex = await Assert.ThrowsAsync<AiException>(() => c.GenerateAsync(AiProviders.OpenAi, "m", "k", "s", "u", default));
        Assert.Equal("bad_response", ex.Code);

        var (c2, _) = Client(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"sem json\"}}]}");
        ex = await Assert.ThrowsAsync<AiException>(() => c2.GenerateAsync(AiProviders.OpenAi, "m", "k", "s", "u", default));
        Assert.Equal("bad_response", ex.Code);
    }

    [Fact]
    public void ParseSuggestion_tolera_cerca_de_codigo()
    {
        var s = AiClient.ParseSuggestion("```json\n{\"sql\":\"SELECT 2\",\"notes\":\"\",\"writesData\":false}\n```");
        Assert.Equal("SELECT 2", s.Sql);
        Assert.Null(s.Notes);
    }

    [Fact]
    public async Task Modelo_com_caracteres_estranhos_e_recusado_antes_de_ir_para_a_rede()
    {
        var (c, h) = Client(HttpStatusCode.OK, "{}");
        await Assert.ThrowsAsync<AiException>(() => c.GenerateAsync(AiProviders.Gemini, "x/../y?z", "k", "s", "u", default));
        Assert.Null(h.Last);
    }

    private static MetadataSnapshot Snapshot(bool schemaLevel = true)
    {
        var objects = new[]
        {
            new MetaObject("dbo", "Clientes", "table"), new MetaObject("dbo", "Pedidos", "table"),
            new MetaObject("dbo", "Logs", "table"), new MetaObject("dbo", "proc_x", "procedure"),
        };
        var cols = new Dictionary<string, IReadOnlyList<MetaColumn>>
        {
            ["dbo.Clientes"] = [new MetaColumn("Id", "int", false), new MetaColumn("Nome", "varchar(50)", true)],
            ["dbo.Pedidos"] = [new MetaColumn("Id", "int", false), new MetaColumn("Total", "decimal(10,2)", false)],
            ["dbo.Logs"] = [new MetaColumn("Texto", "nvarchar(max)", true)],
        };
        return new MetadataSnapshot(["dbo"], objects, cols, true, DateTimeOffset.UtcNow, schemaLevel);
    }

    [Fact]
    public void Esquema_compacto_so_com_tabelas_e_colunas()
    {
        var s = PromptBuilder.Schema(Snapshot(), "x", 10_000);
        Assert.Contains("dbo.Clientes(Id int, Nome varchar(50))", s);
        Assert.DoesNotContain("proc_x", s);
    }

    [Fact]
    public void Esquema_sem_nivel_de_schema_omite_o_prefixo()
    {
        Assert.Contains("Clientes(Id int", PromptBuilder.Schema(Snapshot(schemaLevel: false), "x", 10_000));
        Assert.DoesNotContain("dbo.", PromptBuilder.Schema(Snapshot(schemaLevel: false), "x", 10_000));
    }

    [Fact]
    public void Esquema_acima_do_limite_prioriza_o_que_o_pedido_menciona()
    {
        var full = PromptBuilder.Schema(Snapshot(), "x", 10_000);
        var pedidosLine = full.Split('\n').Select(l => l.TrimEnd()).Single(l => l.StartsWith("dbo.Pedidos"));
        var s = PromptBuilder.Schema(Snapshot(), "total dos pedidos", pedidosLine.Length + 2);
        Assert.Equal(pedidosLine, s);
    }

    [Fact]
    public void Prompt_do_usuario_limita_o_tamanho_do_pedido()
    {
        var u = PromptBuilder.User("sqlserver", Snapshot(), new string('a', 10_000));
        Assert.True(u.Length < 10_000);
    }

    [Fact]
    public void Chave_fica_protegida_no_arquivo_e_nao_volta_na_leitura_publica()
    {
        var path = Path.Combine(_dir, "ai.json");
        var store = new AiSettingsStore(path, new PlainProtector());
        Assert.False(store.Get().HasKey);

        var info = store.Save(AiProviders.OpenAi, "gpt-x", "  sk-segredo  ", false);
        Assert.True(info.HasKey);
        Assert.Equal("sk-segredo", store.GetKey());

        // Salvar sem chave nova mantém a atual; remover apaga.
        store.Save(AiProviders.Gemini, "", null, false);
        Assert.Equal("sk-segredo", store.GetKey());
        Assert.Equal(AiProviders.DefaultModel(AiProviders.Gemini), store.Get().Model);
        Assert.False(store.Save(AiProviders.Gemini, "", null, true).HasKey);
        Assert.Null(store.GetKey());
    }

    [Fact]
    public void Salvar_recusa_provedor_e_modelo_invalidos()
    {
        var store = new AiSettingsStore(Path.Combine(_dir, "ai.json"), new PlainProtector());
        Assert.Throws<ConnectionValidationException>(() => store.Save("outro", "m", null, false));
        Assert.Throws<ConnectionValidationException>(() => store.Save(AiProviders.OpenAi, "a b/c", null, false));
    }
}
