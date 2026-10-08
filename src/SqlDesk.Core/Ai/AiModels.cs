namespace SqlDesk.Core.Ai;

/// <summary>Formato da API: os três primeiros têm adaptador próprio; <see cref="Compatible"/> é o "chat/completions" da OpenAI em outro endereço.</summary>
public enum AiKind { OpenAi, Anthropic, Gemini, Compatible }

/// <param name="BaseUrl">Endereço fixo do provedor (termina em /v1, sem barra final). Nulo: o usuário informa (provedor "custom").</param>
/// <param name="RequiresKey">Falso para modelos locais (Ollama), que dispensam chave.</param>
public sealed record AiProviderInfo(string Id, AiKind Kind, string? BaseUrl, string DefaultModel, bool RequiresKey = true);

public static class AiProviders
{
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";
    public const string Custom = "custom";

    private static readonly AiProviderInfo[] Catalog =
    [
        new(Anthropic, AiKind.Anthropic, null, "claude-haiku-5-5"),
        new(OpenAi, AiKind.OpenAi, null, "gpt-4o-mini"),
        new(Gemini, AiKind.Gemini, null, "gemini-2.0-flash"),
        new("nvidia", AiKind.Compatible, "https://integrate.api.nvidia.com/v1", "meta/llama-3.3-70b-instruct"),
        new("groq", AiKind.Compatible, "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile"),
        new("openrouter", AiKind.Compatible, "https://openrouter.ai/api/v1", "meta-llama/llama-3.3-70b-instruct:free"),
        new("cerebras", AiKind.Compatible, "https://api.cerebras.ai/v1", "llama-3.3-70b"),
        new("mistral", AiKind.Compatible, "https://api.mistral.ai/v1", "mistral-small-latest"),
        new("ollama", AiKind.Compatible, "http://localhost:11434/v1", "llama3.1", RequiresKey: false),
        new(Custom, AiKind.Compatible, null, "", RequiresKey: false),
    ];

    public static IReadOnlyList<AiProviderInfo> All => Catalog;

    public static bool IsKnown(string? id) => Find(id) is not null;

    public static AiProviderInfo? Find(string? id) => Catalog.FirstOrDefault(p => p.Id == id);

    public static AiProviderInfo Get(string id) => Find(id) ?? throw new AiException("bad_provider", "Provedor de IA desconhecido.");

    public static string DefaultModel(string id) => Find(id)?.DefaultModel ?? "";

    /// <summary>
    /// Endereço base de um provedor compatível. O provedor "custom" usa o informado pelo usuário, que só pode ser https
    /// (ou http para a própria máquina): a chave de API vai nesse endereço. Devolve null se for inválido.
    /// </summary>
    public static string? ResolveBaseUrl(AiProviderInfo p, string? custom)
    {
        if (p.BaseUrl is not null) return p.BaseUrl;
        if (!Uri.TryCreate(custom?.Trim(), UriKind.Absolute, out var uri)) return null;
        var local = uri.IsLoopback;
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && local)) return null;
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}

/// <summary>Configuração de um provedor como vista pelo frontend: a chave nunca sai do backend, só <see cref="HasKey"/>.</summary>
public sealed record AiProviderEntry(string Provider, string Model, string? BaseUrl, bool HasKey);

/// <param name="Provider">Provedor ativo.</param>
/// <param name="Entries">Um item por provedor já configurado (modelo, endereço e se tem chave), para a tela trocar de provedor sem perder o que foi digitado antes.</param>
public sealed record AiSettingsInfo(string Provider, string Model, string? BaseUrl, bool HasKey, IReadOnlyList<AiProviderEntry> Entries);

/// <summary>Resposta já interpretada do modelo.</summary>
public sealed record AiSuggestion(string Sql, string? Notes);

/// <param name="ReadOnly">Decidido pela trava (<c>AiSqlGuard</c>), nunca pelo que o modelo afirma.</param>
/// <param name="Reason">Quando <see cref="ReadOnly"/> é falso: por que a trava recusou.</param>
public sealed record AiGenerateResult(string Sql, string? Notes, bool ReadOnly, string? Reason);

/// <summary>Falha da IA com mensagem pronta para exibir. Nunca carrega a chave nem o prompt.</summary>
public sealed class AiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
