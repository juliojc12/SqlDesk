using System.Text.Json;

namespace SqlDesk.Host.Bridge;

public interface IMessageHandler
{
    string Type { get; }

    Task<object?> HandleAsync(JsonElement payload, CancellationToken ct);
}

/// <summary>Handler tipado: desserializa o payload em <typeparamref name="TRequest"/>.</summary>
public abstract class MessageHandler<TRequest, TResponse> : IMessageHandler
{
    public abstract string Type { get; }

    protected abstract Task<TResponse> HandleAsync(TRequest request, CancellationToken ct);

    async Task<object?> IMessageHandler.HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var request = payload.Deserialize<TRequest>(BridgeJson.Options)
            ?? throw new BridgeException("invalid_payload", $"Payload inválido para '{Type}'.");
        return await HandleAsync(request, ct);
    }
}

public sealed class BridgeException(string code, string message, int? line = null) : Exception(message)
{
    public string Code { get; } = code;
    public int? Line { get; } = line;
}
