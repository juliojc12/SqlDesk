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
        var r = await c.GenerateAsync(AiProviders.OpenAi, "gpt-x", null, "KEY", "sys", "usr", default);
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
        var r = await c.GenerateAsync(AiProviders.Anthropic, "claude-x", null, "KEY", "sys", "usr", default);
        Assert.Equal("SELECT 1", r.Sql);
        Assert.Equal("KEY", h.Last!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("https://api.anthropic.com/v1/messages", h.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task Gemini_monta_o_pedido_e_le_a_resposta()
    {
        var (c, h) = Client(HttpStatusCode.OK, "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"" + Inner + "\"}]}}]}");
        var r = await c.GenerateAsync(AiProviders.Gemini, "gemini-x", null, "KEY", "sys", "usr", default);
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
        var ex = await Assert.ThrowsAsync<AiException>(() => c.GenerateAsync(AiProviders.OpenAi, "m", null, "KEY-123", "s", "u", default));
        Assert.Equal(code, ex.Code);
        Assert.DoesNotContain("KEY-123", ex.Message);
        Assert.DoesNotContain("SEGREDO", ex.Message);
    }

    [Fact]
    public async Task Resposta_invalida_vira_bad_response()
    {
        var (c, _) = Client(HttpStatusCode.OK, "isto não é json");
        var ex = await Assert.ThrowsAsync<AiException>(() => c.GenerateAsync(AiProviders.OpenAi, "m", null, "k", "s", "u", default));
        Assert.Equal("bad_response", ex.Code);

        var (c2, _) = Client(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"sem json\"}}]}");
        ex = await Assert.ThrowsAsync<AiException>(() => c2.GenerateAsync(AiProviders.OpenAi, "m", null, "k", "s", "u", default));
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
        await Assert.ThrowsAsync<AiException>(() => c.GenerateAsync(AiProviders.Gemini, "x/../y?z", null, "k", "s", "u", default));
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

        var info = store.Save(AiProviders.OpenAi, "gpt-x", null, "  sk-segredo  ", false);
        Assert.True(info.HasKey);
        Assert.Equal("sk-segredo", store.GetKey());

        // Salvar sem chave nova mantém a atual; remover apaga.
        store.Save(AiProviders.OpenAi, "gpt-y", null, null, false);
        Assert.Equal("sk-segredo", store.GetKey());
        Assert.Equal("gpt-y", store.Get().Model);

        // A chave é de cada provedor: ao trocar, a do outro não vale.
        store.Save(AiProviders.Gemini, "", null, null, false);
        Assert.Null(store.GetKey());
        Assert.Equal(AiProviders.DefaultModel(AiProviders.Gemini), store.Get().Model);
        Assert.True(store.Get().Entries.Single(e => e.Provider == AiProviders.OpenAi).HasKey);

        store.Save(AiProviders.OpenAi, "gpt-y", null, null, true);
        Assert.False(store.Get().HasKey);
        Assert.Null(store.GetKey());
    }

    [Fact]
    public void Salvar_recusa_provedor_e_modelo_invalidos()
    {
        var store = new AiSettingsStore(Path.Combine(_dir, "ai.json"), new PlainProtector());
        Assert.Throws<ConnectionValidationException>(() => store.Save("outro", "m", null, null, false));
        Assert.Throws<ConnectionValidationException>(() => store.Save(AiProviders.OpenAi, "a b/c", null, null, false));
        Assert.Throws<ConnectionValidationException>(() => store.Save("nvidia", "a/../b", null, null, false));
        Assert.Throws<ConnectionValidationException>(() => store.Save(AiProviders.Custom, "", "https://x.com/v1", null, false)); // custom não tem modelo padrão
    }

    [Theory]
    [InlineData("https://api.exemplo.com/v1", "https://api.exemplo.com/v1")]
    [InlineData("https://api.exemplo.com/v1/", "https://api.exemplo.com/v1")]
    [InlineData("http://localhost:1234/v1", "http://localhost:1234/v1")]
    [InlineData("http://127.0.0.1:8000/v1", "http://127.0.0.1:8000/v1")]
    [InlineData("http://api.exemplo.com/v1", null)]
    [InlineData("ftp://x.com/v1", null)]
    [InlineData("https://user:senha@x.com/v1", null)]
    [InlineData("https://x.com/v1?chave=1", null)]
    [InlineData("não é url", null)]
    [InlineData("", null)]
    public void Endereco_personalizado_so_aceita_https_ou_a_propria_maquina(string url, string? expected)
    {
        Assert.Equal(expected, AiProviders.ResolveBaseUrl(AiProviders.Get(AiProviders.Custom), url));
    }

    [Fact]
    public void Provedores_com_endereco_fixo_ignoram_o_informado()
    {
        Assert.Equal("https://integrate.api.nvidia.com/v1", AiProviders.ResolveBaseUrl(AiProviders.Get("nvidia"), "https://outro.com/v1"));
    }

    [Fact]
    public void Salvar_custom_exige_endereco_valido_e_guarda_so_o_normalizado()
    {
        var store = new AiSettingsStore(Path.Combine(_dir, "ai.json"), new PlainProtector());
        Assert.Throws<ConnectionValidationException>(() => store.Save(AiProviders.Custom, "m", "http://remoto.com/v1", null, false));
        var info = store.Save(AiProviders.Custom, "m", "https://x.com/v1/", null, false);
        Assert.Equal("https://x.com/v1", info.BaseUrl);
        var (provider, model, url) = store.Active();
        Assert.Equal((AiProviders.Custom, "m", "https://x.com/v1"), (provider, model, url));
    }

    [Theory]
    [InlineData("nvidia", "https://integrate.api.nvidia.com/v1/chat/completions")]
    [InlineData("groq", "https://api.groq.com/openai/v1/chat/completions")]
    [InlineData("openrouter", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("ollama", "http://localhost:11434/v1/chat/completions")]
    public async Task Provedor_compativel_usa_o_chat_completions_no_endereco_dele(string provider, string url)
    {
        var (c, h) = Client(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"" + Inner + "\"}}]}");
        var r = await c.GenerateAsync(provider, "meta/llama-3.3-70b-instruct", null, "nvapi-KEY", "sys", "usr", default);
        Assert.Equal("SELECT 1", r.Sql);
        Assert.Equal(url, h.Last!.RequestUri!.ToString());
        Assert.Equal("Bearer nvapi-KEY", h.Last.Headers.GetValues("Authorization").Single());
        Assert.DoesNotContain("response_format", h.LastBody);
    }

    [Fact]
    public async Task Sem_chave_nao_manda_cabecalho_de_autorizacao()
    {
        var (c, h) = Client(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"" + Inner + "\"}}]}");
        await c.GenerateAsync("custom", "m", "http://localhost:8080/v1", "", "s", "u", default);
        Assert.False(h.Last!.Headers.Contains("Authorization"));
        Assert.Equal("http://localhost:8080/v1/chat/completions", h.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task Custom_com_endereco_invalido_nao_vai_para_a_rede()
    {
        var (c, h) = Client(HttpStatusCode.OK, "{}");
        var ex = await Assert.ThrowsAsync<AiException>(() => c.GenerateAsync("custom", "m", "http://remoto.com/v1", "k", "s", "u", default));
        Assert.Equal("bad_url", ex.Code);
        Assert.Null(h.Last);
    }

    [Fact]
    public void ParseSuggestion_ignora_o_raciocinio_entre_think()
    {
        var s = AiClient.ParseSuggestion("<think>talvez {\"sql\": \"DROP\"} hmm</think> {\"sql\":\"SELECT 3\",\"notes\":\"\"}");
        Assert.Equal("SELECT 3", s.Sql);
    }

    [Fact]
    public void Modelo_com_barra_e_aceito_mas_nao_com_pontos_duplos()
    {
        Assert.True(AiClient.IsValidModel("meta/llama-3.3-70b-instruct"));
        Assert.True(AiClient.IsValidModel("meta-llama/llama-3.3-70b-instruct:free"));
        Assert.False(AiClient.IsValidModel("a/../b"));
        Assert.False(AiClient.IsValidModel("a/"));
    }

    private sealed class RouterHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri}");
            return Task.FromResult(route(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Modelo_nao_encontrado_em_provedor_compativel_sugere_nomes_parecidos_da_lista()
    {
        var h = new RouterHandler(r => r.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"meta/llama-3.3-70b-instruct\"},{\"id\":\"nvidia/nemotron-3-super-120b-a12b\"}]}")
            : Json(HttpStatusCode.NotFound, "{}"));
        var c = new AiClient(new HttpClient(h));
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            c.GenerateAsync("nvidia", "nvidia/nvidia/nemotron-3-super-120b-a12b", null, "k", "s", "u", default));
        Assert.Equal("not_found", ex.Code);
        Assert.Contains("nvidia/nemotron-3-super-120b-a12b", ex.Message);
        Assert.DoesNotContain("llama", ex.Message);
        Assert.Contains("GET https://integrate.api.nvidia.com/v1/models", h.Requests);
    }

    [Fact]
    public async Task Modelo_nao_encontrado_sem_lista_de_modelos_volta_a_mensagem_simples()
    {
        var h = new RouterHandler(_ => Json(HttpStatusCode.NotFound, "{}"));
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            new AiClient(new HttpClient(h)).GenerateAsync("groq", "x-model", null, "k", "s", "u", default));
        Assert.Equal("not_found", ex.Code);
        Assert.StartsWith("Modelo não encontrado", ex.Message);
    }

    [Fact]
    public void Sugestoes_ignoram_o_prefixo_e_limitam_a_cinco()
    {
        var many = Enumerable.Range(0, 20).Select(i => $"org{i}/modelo-x").ToList();
        var text = AiClient.SimilarModels("a/b/modelo-x", many);
        Assert.Equal(5, text.Split("org").Length - 1);
        Assert.Contains("nenhum parece", AiClient.SimilarModels("zzzzz", ["a/b"]));
    }

    [Fact]
    public async Task Teste_de_modelo_funciona_com_resposta_valida_e_relata_a_falha_sem_lancar()
    {
        var ok = new RouterHandler(_ => Json(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"" + Inner + "\"}}]}"));
        var (good, msg) = await new AiClient(new HttpClient(ok)).TestAsync("nvidia", "nvidia/nemotron-3-super-120b-a12b", null, "k", default);
        Assert.True(good);
        Assert.StartsWith("Funcionou", msg);

        var denied = new RouterHandler(_ => Json(HttpStatusCode.Unauthorized, "{}"));
        var (bad, msg2) = await new AiClient(new HttpClient(denied)).TestAsync("nvidia", "m", null, "k", default);
        Assert.False(bad);
        Assert.Contains("chave", msg2);

        var odd = new RouterHandler(_ => Json(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"olá\"}}]}"));
        var (accepted, _) = await new AiClient(new HttpClient(odd)).TestAsync("nvidia", "m", null, "k", default);
        Assert.True(accepted);
    }

    [Fact]
    public void Chave_de_um_provedor_especifico_pode_ser_lida_sem_ativa_lo()
    {
        var store = new AiSettingsStore(Path.Combine(_dir, "ai.json"), new PlainProtector());
        store.Save("nvidia", "", null, "nvapi-1", false);
        store.Save(AiProviders.Gemini, "", null, null, false);
        Assert.Null(store.GetKey());
        Assert.Equal("nvapi-1", store.GetKey("nvidia"));
        Assert.Null(store.GetKey("groq"));
    }
}
