using System.Text.Json;

namespace SqlDesk.Host.Bridge;

/// <summary>Mapeia <c>type</c> para o handler registrado. Erros viram <see cref="BridgeError"/>, nunca exceção crua.</summary>
public sealed class MessageDispatcher
{
    private readonly Dictionary<string, IMessageHandler> _handlers;

    public MessageDispatcher(IEnumerable<IMessageHandler> handlers)
    {
        _handlers = new Dictionary<string, IMessageHandler>(StringComparer.Ordinal);
        foreach (var h in handlers)
        {
            if (!_handlers.TryAdd(h.Type, h))
                throw new InvalidOperationException($"Handler duplicado para o tipo '{h.Type}'.");
        }
    }

    /// <summary>Devolve o payload da resposta: o resultado do handler ou <c>{ error }</c>.</summary>
    public async Task<object?> DispatchAsync(string type, JsonElement payload, CancellationToken ct = default)
    {
        if (!_handlers.TryGetValue(type, out var handler))
            return new { error = new BridgeError("unknown_type", $"Tipo de mensagem desconhecido: '{type}'.") };

        try
        {
            return await handler.HandleAsync(payload, ct);
        }
        catch (BridgeException ex)
        {
            return new { error = new BridgeError(ex.Code, ex.Message, ex.Line) };
        }
        catch (OperationCanceledException)
        {
            return new { error = new BridgeError("cancelled", "Operação cancelada.") };
        }
        catch (JsonException ex)
        {
            return new { error = new BridgeError("invalid_payload", ex.Message) };
        }
        catch (Exception ex)
        {
            return new { error = new BridgeError("internal_error", ex.Message) };
        }
    }
}
