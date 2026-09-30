using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SqlDesk.Host.Bridge;
using SqlDesk.Host.Handlers;

namespace SqlDesk.Host;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var sc = new ServiceCollection();
        sc.AddSingleton<IMessageHandler, PingHandler>();
        sc.AddSingleton<MessageDispatcher>();
        sc.AddSingleton<WebViewBridge>();
        sc.AddSingleton<MainWindow>();
        _services = sc.BuildServiceProvider();

        _services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
