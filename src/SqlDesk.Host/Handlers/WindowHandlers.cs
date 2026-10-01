using System.Windows;
using SqlDesk.Host.Bridge;

namespace SqlDesk.Host.Handlers;

/// <summary>Ponte entre os handlers e a janela (evita dependência circular MainWindow ↔ dispatcher).</summary>
public sealed class WindowController
{
    private Window? _window;

    public void Attach(Window window) => _window = window;

    public void Minimize() => Run(w => w.WindowState = WindowState.Minimized);

    public void ToggleMaximize() => Run(w => w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);

    public void Close() => Run(w => w.Close());

    /// <summary>Quando verdadeiro, o fechamento não é interceptado por transações abertas (o frontend já as resolveu).</summary>
    public bool AllowClose { get; private set; }

    public void ForceClose()
    {
        AllowClose = true;
        Close();
    }

    private void Run(Action<Window> a)
    {
        if (_window is { } w) w.Dispatcher.Invoke(() => a(w));
    }
}

public sealed class MinimizeWindowHandler(WindowController win) : MessageHandler<EmptyRequest, EmptyResponse>
{
    public override string Type => "window.minimize";

    protected override Task<EmptyResponse> HandleAsync(EmptyRequest r, CancellationToken ct)
    {
        win.Minimize();
        return Task.FromResult(new EmptyResponse());
    }
}

public sealed class ToggleMaximizeWindowHandler(WindowController win) : MessageHandler<EmptyRequest, EmptyResponse>
{
    public override string Type => "window.toggleMaximize";

    protected override Task<EmptyResponse> HandleAsync(EmptyRequest r, CancellationToken ct)
    {
        win.ToggleMaximize();
        return Task.FromResult(new EmptyResponse());
    }
}

public sealed class CloseWindowHandler(WindowController win) : MessageHandler<EmptyRequest, EmptyResponse>
{
    public override string Type => "window.close";

    protected override Task<EmptyResponse> HandleAsync(EmptyRequest r, CancellationToken ct)
    {
        win.Close();
        return Task.FromResult(new EmptyResponse());
    }
}
