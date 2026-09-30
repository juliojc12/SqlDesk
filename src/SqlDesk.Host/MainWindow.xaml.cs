using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using SqlDesk.Host.Bridge;
using SqlDesk.Host.Handlers;

namespace SqlDesk.Host;

public partial class MainWindow : Window
{
    private readonly WebViewBridge _bridge;

    public MainWindow(WebViewBridge bridge, WindowController controller)
    {
        _bridge = bridge;
        controller.Attach(this);
        InitializeComponent();
        StateChanged += OnStateChanged;
        SourceInitialized += (_, _) => WorkAreaHook.Install(this);
        Loaded += async (_, _) => await InitWebAsync();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        _bridge.Publish("window.state", new WindowStateEvent(maximized));
    }

    private async Task InitWebAsync()
    {
        try
        {
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlDesk", "webview");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsNonClientRegionSupportEnabled = true; // CSS app-region: drag
            _bridge.Attach(core);

            var devUrl = Environment.GetEnvironmentVariable("SQLDESK_DEV_URL");
#if DEBUG
            devUrl ??= "http://localhost:5173";
#endif
            if (devUrl is not null)
            {
                core.Navigate(devUrl);
            }
            else
            {
                var folder = WebContent.Extract();
                core.SetVirtualHostNameToFolderMapping(WebContent.VirtualHost, folder, CoreWebView2HostResourceAccessKind.Allow);
                core.Navigate($"https://{WebContent.VirtualHost}/index.html");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não foi possível iniciar o WebView2:\n\n" + ex.Message, "SqlLite Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }
}
