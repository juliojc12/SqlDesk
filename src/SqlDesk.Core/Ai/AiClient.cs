using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SqlDesk.Core.Ai;

/// <summary>
/// Chama a API do provedor (OpenAI, Anthropic ou Gemini) e devolve o SQL sugerido. É a única chamada de saída do app.
/// Nenhuma mensagem de erro inclui a chave, o prompt ou o corpo da resposta do servidor.
/// </summary>
public sealed partial class AiClient(HttpClient http)
{
    public static bool IsValidModel(string model) =>
        model.Length is > 0 and <= 100 && ModelPattern().IsMatch(model) && !model.Contains("..") && !model.EndsWith('/');

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/\-]*$")]
    private static partial Regex ModelPattern();

    /// <param name="baseUrl">Só para provedores compatíveis com a OpenAI (o "custom" informa o seu).</param>
    /// <param name="apiKey">Pode ser vazia em provedores que dispensam chave (modelo local).</param>
    public async Task<AiSuggestion> GenerateAsync(string provider, string model, string? baseUrl, string apiKey, string system, string user, CancellationToken ct)
    {
        if (!IsValidModel(model)) throw new AiException("bad_model", "Nome de modelo inválido nas configurações.");

        using var request = BuildRequest(provider, model, baseUrl, apiKey, system, user);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new AiException("timeout", "A IA demorou demais para responder."); }
        catch (HttpRequestException) { throw new AiException("network", "Não foi possível falar com o provedor de IA. Verifique a conexão com a internet."); }

        using (response)
        {
            if (!response.IsSuccessStatusCode) throw FromStatus(response.StatusCode);
            string body;
            try { body = await response.Content.ReadAsStringAsync(ct); }
            catch (HttpRequestException) { throw new AiException("network", "A conexão com o provedor de IA caiu antes do fim da resposta."); }
            return ParseSuggestion(ExtractText(AiProviders.Get(provider).Kind, body));
        }
    }

    private static HttpRequestMessage BuildRequest(string provider, string model, string? baseUrl, string apiKey, string system, string user)
    {
        var info = AiProviders.Get(provider);
        HttpRequestMessage req;
        switch (info.Kind)
        {
            case AiKind.Compatible:
            {
                var root = AiProviders.ResolveBaseUrl(info, baseUrl)
                    ?? throw new AiException("bad_url", "Endereço da API inválido nas configurações.");
                req = new HttpRequestMessage(HttpMethod.Post, root + "/chat/completions");
                if (apiKey.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                // Sem response_format: nem todo servidor compatível aceita; o prompt já exige JSON e a leitura tolera cercas.
                req.Content = Json(new JsonObject
                {
                    ["model"] = model,
                    ["temperature"] = 0,
                    ["messages"] = new JsonArray(
                        new JsonObject { ["role"] = "system", ["content"] = system },
                        new JsonObject { ["role"] = "user", ["content"] = user }),
                });
                break;
            }
            case AiKind.OpenAi:
                req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                req.Content = Json(new JsonObject
                {
                    ["model"] = model,
                    ["response_format"] = new JsonObject { ["type"] = "json_object" },
                    ["messages"] = new JsonArray(
                        new JsonObject { ["role"] = "system", ["content"] = system },
                        new JsonObject { ["role"] = "user", ["content"] = user }),
                });
                break;
            case AiKind.Anthropic:
                req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                req.Content = Json(new JsonObject
                {
                    ["model"] = model,
                    ["max_tokens"] = 2048,
                    ["system"] = system,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = user }),
                });
                break;
            case AiKind.Gemini:
                req = new HttpRequestMessage(HttpMethod.Post,
                    $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent");
                req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                req.Content = Json(new JsonObject
                {
                    ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
                    ["contents"] = new JsonArray(new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray(new JsonObject { ["text"] = user }),
                    }),
                    ["generationConfig"] = new JsonObject { ["responseMimeType"] = "application/json" },
                });
                break;
            default:
                throw new AiException("bad_provider", "Provedor de IA desconhecido.");
        }
        return req;
    }

    private static StringContent Json(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    private static AiException FromStatus(HttpStatusCode status) => (int)status switch
    {
        400 => new AiException("bad_request", "O provedor recusou o pedido (400). Confira o nome do modelo nas configurações."),
        401 or 403 => new AiException("unauthorized", "O provedor recusou a chave de API. Confira a chave nas configurações."),
        404 => new AiException("not_found", "Modelo não encontrado no provedor. Confira o nome do modelo nas configurações."),
        429 => new AiException("rate_limit", "Limite de uso do provedor atingido. Tente de novo em instantes."),
        >= 500 => new AiException("server", $"O provedor de IA está com problemas ({(int)status}). Tente de novo em instantes."),
        _ => new AiException("http", $"O provedor de IA respondeu com erro {(int)status}."),
    };

    private static string ExtractText(AiKind kind, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var text = kind switch
            {
                AiKind.OpenAi or AiKind.Compatible => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(),
                AiKind.Anthropic => root.GetProperty("content").EnumerateArray()
                    .Where(p => p.TryGetProperty("type", out var t) && t.GetString() == "text")
                    .Select(p => p.GetProperty("text").GetString()).FirstOrDefault(),
                _ => root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString(),
            };
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { }
        throw new AiException("bad_response", "O provedor de IA devolveu uma resposta que não deu para entender.");
    }

    /// <summary>Lê <c>{"sql": "...", "notes": "..."}</c>, tolerando cercas de código (```json) em volta.</summary>
    public static AiSuggestion ParseSuggestion(string text)
    {
        // Modelos que "pensam em voz alta" (DeepSeek, Qwen...) devolvem o raciocínio entre <think> antes da resposta.
        text = ThinkBlock().Replace(text, "");
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("sql", out var sql) && sql.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(sql.GetString()))
                {
                    var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    return new AiSuggestion(sql.GetString()!.Trim(), string.IsNullOrWhiteSpace(notes) ? null : notes.Trim());
                }
            }
            catch (JsonException) { }
        }
        throw new AiException("bad_response", "A IA não devolveu o SQL no formato esperado. Tente reescrever o pedido.");
    }
}
