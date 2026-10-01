namespace SqlDesk.Host.Bridge;

/// <summary>
/// Ponto de publicação de eventos (C# para JS, sem requisição) para quem roda dentro de um handler. Existe para
/// que os handlers não dependam do <see cref="WebViewBridge"/>, que depende do dispatcher que os contém.
/// </summary>
public sealed class EventHub
{
    private volatile Action<string, object?>? _sink;

    public void Attach(Action<string, object?> sink) => _sink = sink;

    public void Publish(string type, object? payload) => _sink?.Invoke(type, payload);
}
