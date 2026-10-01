using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using SqlDesk.Core.Execution;
using SqlDesk.Core.Export;
using SqlDesk.Core.Sessions;
using SqlDesk.Host.Bridge;

namespace SqlDesk.Host.Handlers;

/// <summary>Exportações em andamento (para cancelar) e arquivos que o app gerou (únicos que "Abrir arquivo" e "Abrir pasta" aceitam).</summary>
public sealed class ExportRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<string, byte> _exported = new(StringComparer.OrdinalIgnoreCase);

    public string? LastDirectory { get; set; }

    public CancellationTokenSource Start(string exportId)
    {
        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(exportId, cts))
        {
            cts.Dispose();
            throw new BridgeException("busy", "Já existe uma exportação com este identificador.");
        }
        return cts;
    }

    public void Finish(string exportId)
    {
        if (_running.TryRemove(exportId, out var cts)) cts.Dispose();
    }

    public void Cancel(string exportId)
    {
        if (_running.TryGetValue(exportId, out var cts))
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* terminou agora */ }
        }
    }

    public void Remember(string path) => _exported[Path.GetFullPath(path)] = 0;

    public bool Knows(string path) => _exported.ContainsKey(Path.GetFullPath(path));
}

internal static class ExportParsing
{
    public static ExportFormat Format(string value) =>
        value.Equals("xlsx", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Xlsx
        : value.Equals("csv", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Csv
        : throw new BridgeException("invalid_payload", $"Formato de exportação desconhecido: '{value}'.");

    public static char Delimiter(string value) => value switch
    {
        "," => ',',
        "\t" or "tab" => '\t',
        _ => ';',
    };

    /// <summary>Valor JSON de uma célula para o tipo que o gravador espera (texto, número, bit ou NULL).</summary>
    public static object? Cell(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        _ => e.GetRawText(),
    };

    public static BridgeException Map(Exception ex) => ex switch
    {
        ExportLimitException => new BridgeException("export_limit", ex.Message),
        ExportFailedException => new BridgeException("export_failed", ex.Message),
        TabNotConnectedException => new BridgeException("not_connected", ex.Message),
        TabBusyException => new BridgeException("busy", ex.Message),
        IOException or UnauthorizedAccessException => new BridgeException("file_error", $"Não foi possível gravar o arquivo: {ex.Message}"),
        _ => throw ex,
    };
}

/// <summary>"Salvar como" nativo do Windows, aberto pelo host; o frontend só recebe o caminho escolhido.</summary>
public sealed class ExportPickPathHandler(ExportRegistry registry) : MessageHandler<ExportPickRequest, ExportPickResponse>
{
    public override string Type => "export.pickPath";

    protected override Task<ExportPickResponse> HandleAsync(ExportPickRequest r, CancellationToken ct)
    {
        var format = ExportParsing.Format(r.Format);
        var dlg = new SaveFileDialog
        {
            Filter = format == ExportFormat.Xlsx ? "Planilha do Excel (*.xlsx)|*.xlsx" : "CSV (*.csv)|*.csv",
            DefaultExt = format == ExportFormat.Xlsx ? ".xlsx" : ".csv",
            AddExtension = true,
            FileName = r.SuggestedName,
            InitialDirectory = registry.LastDirectory,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return Task.FromResult(new ExportPickResponse(true, null));
        registry.LastDirectory = Path.GetDirectoryName(dlg.FileName);
        return Task.FromResult(new ExportPickResponse(false, dlg.FileName));
    }
}

/// <summary>Exporta as linhas que o frontend já tem (resultado carregado), na ordem de linhas e colunas que ele enviou.</summary>
public sealed class ExportLoadedHandler(ExportRegistry registry, EventHub hub) : MessageHandler<ExportLoadedRequest, ExportDoneResponse>
{
    public override string Type => "export.loaded";

    protected override async Task<ExportDoneResponse> HandleAsync(ExportLoadedRequest r, CancellationToken ct)
    {
        var format = ExportParsing.Format(r.Format);
        var delimiter = ExportParsing.Delimiter(r.Delimiter);
        var cts = registry.Start(r.ExportId);
        try
        {
            var rows = r.Rows.Select(row => row.Select(ExportParsing.Cell).ToArray());
            var result = await Task.Run(() => ExportService.ExportLoadedAsync(
                format, r.Path, delimiter, r.Columns, rows, n => hub.Publish("export.progress", new ExportProgressEvent(r.ExportId, n)), cts.Token), cts.Token);
            registry.Remember(result.Path);
            return new ExportDoneResponse(result.Path, result.Rows, result.ElapsedMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw ExportParsing.Map(ex); }
        finally { registry.Finish(r.ExportId); }
    }
}

/// <summary>Reexecuta o texto na conexão da aba e grava tudo (sem o limite da grade) direto no arquivo, em streaming.</summary>
public sealed class ExportRerunHandler(ExportRegistry registry, QueryRunner runner, EventHub hub) : MessageHandler<ExportRerunRequest, ExportDoneResponse>
{
    public override string Type => "export.rerun";

    protected override async Task<ExportDoneResponse> HandleAsync(ExportRerunRequest r, CancellationToken ct)
    {
        var format = ExportParsing.Format(r.Format);
        var delimiter = ExportParsing.Delimiter(r.Delimiter);
        var cts = registry.Start(r.ExportId);
        try
        {
            // Task.Run: sem o contexto da interface, as continuações do leitor e a escrita ficam no pool de threads.
            var result = await Task.Run(() => ExportService.ExportByRerunAsync(
                runner, r.TabId, r.SourceText, format, r.Path, delimiter, r.ResultOrdinal, r.ColumnOrder,
                n => hub.Publish("export.progress", new ExportProgressEvent(r.ExportId, n)), cts.Token), cts.Token);
            registry.Remember(result.Path);
            return new ExportDoneResponse(result.Path, result.Rows, result.ElapsedMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw ExportParsing.Map(ex); }
        finally { registry.Finish(r.ExportId); }
    }
}

public sealed class ExportCancelHandler(ExportRegistry registry) : MessageHandler<ExportCancelRequest, EmptyResponse>
{
    public override string Type => "export.cancel";

    protected override Task<EmptyResponse> HandleAsync(ExportCancelRequest r, CancellationToken ct)
    {
        registry.Cancel(r.ExportId);
        return Task.FromResult(new EmptyResponse());
    }
}

/// <summary>Abre no programa padrão um arquivo que o app acabou de exportar (e só ele).</summary>
public sealed class OpenExportedFileHandler(ExportRegistry registry) : MessageHandler<PathRequest, EmptyResponse>
{
    public override string Type => "export.openFile";

    protected override Task<EmptyResponse> HandleAsync(PathRequest r, CancellationToken ct)
    {
        if (!registry.Knows(r.Path) || !File.Exists(r.Path)) throw new BridgeException("file_error", "O arquivo não existe mais ou não foi gerado por este aplicativo.");
        Process.Start(new ProcessStartInfo(r.Path) { UseShellExecute = true });
        return Task.FromResult(new EmptyResponse());
    }
}

/// <summary>Mostra o arquivo exportado no Explorer.</summary>
public sealed class ShowExportedFileHandler(ExportRegistry registry) : MessageHandler<PathRequest, EmptyResponse>
{
    public override string Type => "export.showInFolder";

    protected override Task<EmptyResponse> HandleAsync(PathRequest r, CancellationToken ct)
    {
        if (!registry.Knows(r.Path) || !File.Exists(r.Path)) throw new BridgeException("file_error", "O arquivo não existe mais ou não foi gerado por este aplicativo.");
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", Path.GetFullPath(r.Path) }, UseShellExecute = false });
        return Task.FromResult(new EmptyResponse());
    }
}
