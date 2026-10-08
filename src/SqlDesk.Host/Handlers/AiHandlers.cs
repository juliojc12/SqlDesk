using System.Collections.Concurrent;
using SqlDesk.Core.Ai;
using SqlDesk.Core.Connections;
using SqlDesk.Host.Bridge;

namespace SqlDesk.Host.Handlers;

public sealed class AiSettingsGetHandler(AiSettingsStore store) : MessageHandler<EmptyRequest, AiSettingsDto>
{
    public override string Type => "ai.settings.get";

    protected override Task<AiSettingsDto> HandleAsync(EmptyRequest r, CancellationToken ct)
    {
        var s = store.Get();
        return Task.FromResult(new AiSettingsDto(s.Provider, s.Model, s.HasKey));
    }
}

public sealed class AiSettingsSaveHandler(AiSettingsStore store) : MessageHandler<AiSaveSettingsRequest, AiSettingsDto>
{
    public override string Type => "ai.settings.save";

    protected override Task<AiSettingsDto> HandleAsync(AiSaveSettingsRequest r, CancellationToken ct) =>
        Guard.Run(() =>
        {
            var s = store.Save(r.Provider, r.Model, r.ApiKey, r.RemoveKey);
            return new AiSettingsDto(s.Provider, s.Model, s.HasKey);
        });
}

/// <summary>Gerações em andamento por aba (uma por vez), para o botão de cancelar.</summary>
public sealed class AiRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public CancellationTokenSource Start(string tabId)
    {
        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(tabId, cts))
        {
            cts.Dispose();
            throw new BridgeException("busy", "Esta aba já está gerando uma consulta com IA.");
        }
        return cts;
    }

    public void Finish(string tabId)
    {
        if (_running.TryRemove(tabId, out var cts)) cts.Dispose();
    }

    public void Cancel(string tabId)
    {
        if (_running.TryGetValue(tabId, out var cts))
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* terminou agora */ }
        }
    }
}

public sealed class AiGenerateHandler(AiService ai, AiRegistry registry) : MessageHandler<AiGenerateRequest, AiGenerateResponse>
{
    public override string Type => "ai.generate";

    protected override async Task<AiGenerateResponse> HandleAsync(AiGenerateRequest r, CancellationToken ct)
    {
        var cts = registry.Start(r.TabId);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            var res = await ai.GenerateAsync(r.ConnectionId, r.Prompt, linked.Token);
            return new AiGenerateResponse(res.Sql, res.Notes, res.ReadOnly, res.Reason);
        }
        catch (AiException ex) { throw new BridgeException(ex.Code, ex.Message); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw new BridgeException("cancelled", "Geração cancelada."); }
        finally { registry.Finish(r.TabId); }
    }
}

public sealed class AiCancelHandler(AiRegistry registry) : MessageHandler<TabIdRequest, EmptyResponse>
{
    public override string Type => "ai.cancel";

    protected override Task<EmptyResponse> HandleAsync(TabIdRequest r, CancellationToken ct)
    {
        registry.Cancel(r.TabId);
        return Task.FromResult(new EmptyResponse());
    }
}
