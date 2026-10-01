using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SqlDesk.Host.Bridge;

/// <summary>Liga o <see cref="CoreWebView2"/> ao dispatcher. Envelope: <c>{ id, type, payload }</c>.</summary>
public sealed class WebViewBridge(MessageDispatcher dispatcher, EventHub events)
{
    private CoreWebView2? _web;
    private SynchronizationContext? _ui;

    public void Attach(CoreWebView2 web)
    {
        _web = web;
        _ui = SynchronizationContext.Current;
        web.WebMessageReceived += OnMessage;
        events.Attach(Publish);
    }

    public bool IsAttached => _web is not null;

    /// <summary>Evento sem requisição (id nulo).</summary>
    public void Publish(string type, object? payload) => Send(null, type, payload);

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? id = null;
        var type = "";
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            type = root.GetProperty("type").GetString() ?? "";
            var payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;
            var result = await dispatcher.DispatchAsync(type, payload);
            if (id is not null) Send(id, type, result);
        }
        catch (Exception ex)
        {
            if (id is not null)
                Send(id, type, new { error = new BridgeError("bad_envelope", ex.Message) });
        }
    }

    private void Send(string? id, string type, object? payload)
    {
        var json = JsonSerializer.Serialize(new { id, type, payload }, BridgeJson.Options);
        void Post()
        {
            try { _web?.PostWebMessageAsJson(json); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // A janela está fechando e o WebView já foi descartado: não há mais para quem enviar.
            }
        }
        if (_ui is not null && SynchronizationContext.Current != _ui) _ui.Post(_ => Post(), null);
        else Post();
    }
}
