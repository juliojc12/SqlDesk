using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SqlDesk.Core.Connections;
using SqlDesk.Core.Sessions;
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

        // Serviços do Core
        sc.AddSingleton<IPasswordProtector, DpapiPasswordProtector>();
        sc.AddSingleton(sp => new ConnectionStore(ConnectionStore.DefaultPath, sp.GetRequiredService<IPasswordProtector>()));
        sc.AddSingleton<TabSessionManager>();
        sc.AddSingleton(new SessionStateStore(SessionStateStore.DefaultPath));
        sc.AddSingleton<WindowController>();

        // Handlers da ponte
        foreach (var handler in new[]
        {
            typeof(PingHandler),
            typeof(ListConnectionsHandler), typeof(SaveConnectionHandler), typeof(DeleteConnectionHandler),
            typeof(DuplicateConnectionHandler), typeof(TestConnectionHandler),
            typeof(ParseConnectionStringHandler), typeof(BuildConnectionStringHandler),
            typeof(OpenTabHandler), typeof(DisconnectTabHandler), typeof(DisconnectConnectionHandler),
            typeof(LoadSessionStateHandler), typeof(SaveSessionStateHandler),
            typeof(SaveFileHandler), typeof(OpenFileHandler),
            typeof(MinimizeWindowHandler), typeof(ToggleMaximizeWindowHandler), typeof(CloseWindowHandler),
        })
        {
            sc.AddSingleton(typeof(IMessageHandler), handler);
        }

        sc.AddSingleton<MessageDispatcher>();
        sc.AddSingleton<WebViewBridge>();
        sc.AddSingleton<MainWindow>();
        _services = sc.BuildServiceProvider();

        MainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
