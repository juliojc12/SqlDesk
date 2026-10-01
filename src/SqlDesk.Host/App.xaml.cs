using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SqlDesk.Core.Connections;
using SqlDesk.Core.Execution;
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
        sc.AddSingleton<QueryRunner>();
        sc.AddSingleton<IBatchRunner>(sp => sp.GetRequiredService<QueryRunner>());
        sc.AddSingleton<ISessionDb, SqlSessionDb>();
        sc.AddSingleton<TransactionService>();
        sc.AddSingleton(sp => new GuardedRunner(sp.GetRequiredService<ISessionDb>(), sp.GetRequiredService<IBatchRunner>()));
        sc.AddSingleton<TransactionNotifier>();
        sc.AddSingleton(sp =>
        {
            var sessions = sp.GetRequiredService<TabSessionManager>();
            return new SqlDesk.Core.Metadata.MetadataService((id, ct) => sessions.OpenSideConnectionAsync(id, ct));
        });
        sc.AddSingleton(new SessionStateStore(SessionStateStore.DefaultPath));
        sc.AddSingleton<WindowController>();
        sc.AddSingleton<EventHub>();

        // Handlers da ponte
        foreach (var handler in new[]
        {
            typeof(PingHandler),
            typeof(ListConnectionsHandler), typeof(SaveConnectionHandler), typeof(DeleteConnectionHandler),
            typeof(DuplicateConnectionHandler), typeof(TestConnectionHandler),
            typeof(ParseConnectionStringHandler), typeof(BuildConnectionStringHandler),
            typeof(OpenTabHandler), typeof(DisconnectTabHandler), typeof(DisconnectConnectionHandler),
            typeof(LoadSessionStateHandler), typeof(SaveSessionStateHandler),
            typeof(ExecuteHandler), typeof(CancelHandler), typeof(GuardResolveHandler),
            typeof(BeginTranHandler), typeof(CommitTranHandler), typeof(RollbackTranHandler), typeof(ForceCloseHandler),
            typeof(MetadataRefreshHandler), typeof(MetadataGetHandler),
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

        // Conexão fechada: o servidor desfaz a transação; decisões pendentes e contagens deixam de valer.
        var sessions = _services.GetRequiredService<TabSessionManager>();
        var guard = _services.GetRequiredService<GuardedRunner>();
        var tran = _services.GetRequiredService<TransactionService>();
        sessions.TabDisconnected += id =>
        {
            guard.Discard(id);
            tran.Forget(id);
        };

        MainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
