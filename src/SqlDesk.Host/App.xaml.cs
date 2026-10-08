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
        sc.AddSingleton(new SqlDesk.Core.Diagnostics.ErrorLog(SqlDesk.Core.Diagnostics.ErrorLog.DefaultPath));
        sc.AddSingleton<IPasswordProtector, DpapiPasswordProtector>();
        sc.AddSingleton(sp => new ConnectionStore(ConnectionStore.DefaultPath, sp.GetRequiredService<IPasswordProtector>()));
        sc.AddSingleton<TabSessionManager>();
        sc.AddSingleton<QueryRunner>();
        sc.AddSingleton<IBatchRunner>(sp => sp.GetRequiredService<QueryRunner>());
        sc.AddSingleton<ISessionDb, SessionDb>();
        sc.AddSingleton<TransactionService>();
        sc.AddSingleton(sp => new GuardedRunner(sp.GetRequiredService<ISessionDb>(), sp.GetRequiredService<IBatchRunner>()));
        sc.AddSingleton<TransactionNotifier>();
        sc.AddSingleton<ExportRegistry>();
        sc.AddSingleton(sp =>
        {
            var sessions = sp.GetRequiredService<TabSessionManager>();
            return new SqlDesk.Core.Metadata.MetadataService((id, ct) => sessions.OpenSideConnectionAsync(id, ct));
        });
        sc.AddSingleton(new SessionStateStore(SessionStateStore.DefaultPath));
        sc.AddSingleton(sp => new SqlDesk.Core.Ai.AiSettingsStore(SqlDesk.Core.Ai.AiSettingsStore.DefaultPath, new DpapiPasswordProtector("SqlDesk.ai.v1")));
        sc.AddSingleton(new SqlDesk.Core.Ai.AiClient(new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) }));
        sc.AddSingleton<SqlDesk.Core.Ai.AiService>();
        sc.AddSingleton<AiRegistry>();
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
            typeof(ExportPickPathHandler), typeof(ExportLoadedHandler), typeof(ExportRerunHandler), typeof(ExportCancelHandler),
            typeof(OpenExportedFileHandler), typeof(ShowExportedFileHandler),
            typeof(AiSettingsGetHandler), typeof(AiSettingsSaveHandler), typeof(AiGenerateHandler), typeof(AiCancelHandler),
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

        // Erros que escapam de tudo: ficam registrados em %APPDATA%\SqlDesk\logs\error.log. A interface avisa e continua aberta
        // (transações abertas e o texto das abas não se perdem por causa de uma falha de tela).
        var errorLog = _services.GetRequiredService<SqlDesk.Core.Diagnostics.ErrorLog>();
        DispatcherUnhandledException += (_, args) =>
        {
            errorLog.Write("Erro não tratado na interface (WPF)", args.Exception);
            MessageBox.Show($"Ocorreu um erro inesperado: {args.Exception.Message}\n\nO aplicativo continua aberto. Detalhes em:\n{errorLog.FilePath}",
                "SqlLite Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            errorLog.Write("Erro fatal não tratado", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            errorLog.Write("Tarefa em segundo plano falhou", args.Exception);
            args.SetObserved();
        };

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
        // O container só sabe descartar com DisposeAsync (as sessões das abas são IAsyncDisposable). Roda fora da thread da interface
        // para não travar o encerramento esperando as conexões fecharem.
        if (_services is { } services) Task.Run(() => services.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
