using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SqlDesk.Core.Connections;
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
        sc.AddSingleton<IPasswordProtector, DpapiPasswordProtector>();
        sc.AddSingleton(sp => new ConnectionStore(ConnectionStore.DefaultPath, sp.GetRequiredService<IPasswordProtector>()));
        sc.AddSingleton<IMessageHandler, ListConnectionsHandler>();
        sc.AddSingleton<IMessageHandler, SaveConnectionHandler>();
        sc.AddSingleton<IMessageHandler, DeleteConnectionHandler>();
        sc.AddSingleton<IMessageHandler, DuplicateConnectionHandler>();
        sc.AddSingleton<IMessageHandler, TestConnectionHandler>();
        sc.AddSingleton<IMessageHandler, ParseConnectionStringHandler>();
        sc.AddSingleton<IMessageHandler, BuildConnectionStringHandler>();
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
