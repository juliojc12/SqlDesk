using SqlDesk.Core.Connections;
using SqlDesk.Core.Metadata;
using SqlDesk.Core.Providers;
using SqlDesk.SqlAnalysis;

namespace SqlDesk.Core.Ai;

/// <summary>Junta configurações, esquema, chamada ao provedor e a trava de somente leitura. Nunca executa nada no banco.</summary>
public sealed class AiService(AiSettingsStore settings, ConnectionStore connections, MetadataService metadata, AiClient client)
{
    public async Task<AiGenerateResult> GenerateAsync(Guid connectionId, string request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request)) throw new AiException("empty", "Escreva o que você quer consultar.");

        var (providerId, model, baseUrl) = settings.Active();
        var key = settings.GetKey();
        if (key is null && AiProviders.Get(providerId).RequiresKey)
            throw new AiException("no_key", "Cadastre a chave de API em Configurações → Consulta com IA.");

        var conn = connections.Get(connectionId) ?? throw new AiException("no_connection", "Conexão não encontrada.");
        var provider = ProviderRegistry.For(conn.Settings);

        var suggestion = await client.GenerateAsync(
            providerId, model, baseUrl, key ?? "",
            PromptBuilder.System(provider.Id),
            PromptBuilder.User(provider.Id, metadata.Get(connectionId), request), ct);

        // A palavra final é da trava, não do que o modelo diz sobre si mesmo.
        var ok = AiSqlGuard.Check(provider.Analyzer, suggestion.Sql, out var reason);
        return new AiGenerateResult(suggestion.Sql, suggestion.Notes, ok, ok ? null : reason);
    }
}
