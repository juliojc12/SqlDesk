namespace SqlDesk.Core.Ai;

public static class AiProviders
{
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";

    public static readonly string[] All = [OpenAi, Anthropic, Gemini];

    public static bool IsKnown(string? id) => id is not null && Array.IndexOf(All, id) >= 0;

    public static string DefaultModel(string id) => id switch
    {
        OpenAi => "gpt-4o-mini",
        Gemini => "gemini-2.0-flash",
        _ => "claude-haiku-5-5",
    };
}

/// <summary>Configuração como vista pelo frontend: a chave nunca sai do backend, só <see cref="HasKey"/>.</summary>
public sealed record AiSettingsInfo(string Provider, string Model, bool HasKey);

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
