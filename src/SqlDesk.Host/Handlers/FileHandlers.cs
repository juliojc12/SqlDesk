using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using SqlDesk.Host.Bridge;

namespace SqlDesk.Host.Handlers;

// Os diálogos nativos de arquivo são abertos pelo host; o frontend só recebe caminho/conteúdo.
public sealed class SaveFileHandler : MessageHandler<SaveFileRequest, SaveFileResponse>
{
    private const string Filter = "Scripts SQL (*.sql)|*.sql|Todos os arquivos (*.*)|*.*";

    public override string Type => "files.save";

    protected override Task<SaveFileResponse> HandleAsync(SaveFileRequest r, CancellationToken ct)
    {
        var path = r.SaveAs ? null : r.Path;
        if (string.IsNullOrEmpty(path))
        {
            var dlg = new SaveFileDialog
            {
                Filter = Filter,
                DefaultExt = ".sql",
                AddExtension = true,
                FileName = r.SuggestedName,
                InitialDirectory = !string.IsNullOrEmpty(r.Path) ? Path.GetDirectoryName(r.Path) : null,
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog(Application.Current.MainWindow) != true)
                return Task.FromResult(new SaveFileResponse(true, null, null));
            path = dlg.FileName;
        }

        try
        {
            // UTF-8 com BOM: abre com acentos corretos no SSMS e em outros editores do Windows.
            File.WriteAllText(path, r.Content, new UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BridgeException("file_error", $"Não foi possível salvar '{path}': {ex.Message}");
        }
        return Task.FromResult(new SaveFileResponse(false, path, Path.GetFileName(path)));
    }
}

public sealed class OpenFileHandler : MessageHandler<EmptyRequest, OpenFileResponse>
{
    private const long MaxBytes = 20 * 1024 * 1024;

    public override string Type => "files.open";

    protected override Task<OpenFileResponse> HandleAsync(EmptyRequest r, CancellationToken ct)
    {
        var dlg = new OpenFileDialog { Filter = "Scripts SQL (*.sql)|*.sql|Todos os arquivos (*.*)|*.*", CheckFileExists = true };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true)
            return Task.FromResult(new OpenFileResponse(true, null, null, null));

        try
        {
            if (new FileInfo(dlg.FileName).Length > MaxBytes)
                throw new BridgeException("file_too_large", "O arquivo tem mais de 20 MB e não pode ser aberto no editor.");
            return Task.FromResult(new OpenFileResponse(false, dlg.FileName, Path.GetFileName(dlg.FileName), File.ReadAllText(dlg.FileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BridgeException("file_error", $"Não foi possível abrir '{dlg.FileName}': {ex.Message}");
        }
    }
}
