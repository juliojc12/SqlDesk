using System.Text.Json;
using SqlDesk.Core.Connections;

namespace SqlDesk.Core.Ai;

/// <summary>
/// Provedor ativo e, para cada provedor, modelo, endereço e chave de API. A chave de cada provedor fica protegida (DPAPI) e só o
/// backend a lê; nunca é usada com outro provedor (trocar de provedor não manda a chave de um para o endereço de outro).
/// </summary>
public sealed class AiSettingsStore(string path, IPasswordProtector protector)
{
    private readonly object _gate = new();

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlDesk", "ai.json");

    public AiSettingsInfo Get()
    {
        lock (_gate) return Info(Read());
    }

    /// <summary>Modelo e endereço do provedor ativo.</summary>
    public (string Provider, string Model, string? BaseUrl) Active()
    {
        lock (_gate)
        {
            var f = Read();
            var e = f.Entry(f.Active);
            return (f.Active, e.Model, e.BaseUrl);
        }
    }

    /// <summary>Chave do provedor ativo em texto puro, ou null se não houver (ou não puder ser lida, por exemplo em outro usuário do Windows).</summary>
    public string? GetKey()
    {
        lock (_gate)
        {
            var f = Read();
            var protectedKey = f.Entry(f.Active).KeyProtected;
            if (string.IsNullOrEmpty(protectedKey)) return null;
            try { return protector.Unprotect(protectedKey); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException) { return null; }
        }
    }

    /// <param name="newKey">Texto da nova chave; nulo ou vazio mantém a atual deste provedor.</param>
    public AiSettingsInfo Save(string provider, string model, string? baseUrl, string? newKey, bool removeKey)
    {
        var info = AiProviders.Find(provider) ?? throw new ConnectionValidationException("Provedor de IA desconhecido.");
        model = model.Trim();
        if (model.Length == 0) model = info.DefaultModel;
        if (!AiClient.IsValidModel(model)) throw new ConnectionValidationException(
            model.Length == 0 ? "Informe o nome do modelo." : "Nome de modelo inválido.");

        string? url = null;
        if (info.Id == AiProviders.Custom)
        {
            url = AiProviders.ResolveBaseUrl(info, baseUrl)
                ?? throw new ConnectionValidationException("Informe o endereço da API (https://..., ou http:// só para a própria máquina).");
        }

        lock (_gate)
        {
            var f = Read();
            var e = f.Entry(provider);
            e.Model = model;
            e.BaseUrl = url;
            if (removeKey) e.KeyProtected = null;
            else if (!string.IsNullOrWhiteSpace(newKey)) e.KeyProtected = protector.Protect(newKey.Trim());
            f.Active = provider;
            f.Entries[provider] = e;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(f));
            File.Move(tmp, path, overwrite: true);
            return Info(f);
        }
    }

    private static AiSettingsInfo Info(FileModel f)
    {
        var active = f.Entry(f.Active);
        var entries = f.Entries.Select(kv => new AiProviderEntry(kv.Key, kv.Value.Model, kv.Value.BaseUrl, !string.IsNullOrEmpty(kv.Value.KeyProtected))).ToList();
        return new AiSettingsInfo(f.Active, active.Model, active.BaseUrl, !string.IsNullOrEmpty(active.KeyProtected), entries);
    }

    private FileModel Read()
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path)) is { } f && AiProviders.IsKnown(f.Active))
            {
                f.Entries = f.Entries.Where(kv => AiProviders.IsKnown(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
                return f;
            }
        }
        catch (JsonException) { }
        return new FileModel();
    }

    private sealed class EntryModel
    {
        public string Model { get; set; } = "";
        public string? BaseUrl { get; set; }
        public string? KeyProtected { get; set; }
    }

    private sealed class FileModel
    {
        public string Active { get; set; } = AiProviders.Anthropic;
        public Dictionary<string, EntryModel> Entries { get; set; } = [];

        /// <summary>Entrada do provedor; se ainda não existe, com o modelo padrão (sem gravar).</summary>
        public EntryModel Entry(string provider)
        {
            if (!Entries.TryGetValue(provider, out var e)) e = new EntryModel { Model = AiProviders.DefaultModel(provider) };
            if (string.IsNullOrWhiteSpace(e.Model)) e.Model = AiProviders.DefaultModel(provider);
            return e;
        }
    }
}
