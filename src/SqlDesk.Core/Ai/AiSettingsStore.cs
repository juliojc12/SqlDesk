using System.Text.Json;
using SqlDesk.Core.Connections;

namespace SqlDesk.Core.Ai;

/// <summary>Provedor, modelo e chave de API da consulta com IA. A chave fica protegida (DPAPI) e só o backend a lê.</summary>
public sealed class AiSettingsStore(string path, IPasswordProtector protector)
{
    private readonly object _gate = new();

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlDesk", "ai.json");

    public AiSettingsInfo Get()
    {
        lock (_gate)
        {
            var f = Read();
            return new AiSettingsInfo(f.Provider, f.Model, !string.IsNullOrEmpty(f.KeyProtected));
        }
    }

    /// <summary>Chave em texto puro, ou null se não houver (ou se não puder ser lida, por exemplo em outro usuário do Windows).</summary>
    public string? GetKey()
    {
        lock (_gate)
        {
            var f = Read();
            if (string.IsNullOrEmpty(f.KeyProtected)) return null;
            try { return protector.Unprotect(f.KeyProtected); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException) { return null; }
        }
    }

    /// <param name="newKey">Texto da nova chave; nulo ou vazio mantém a atual.</param>
    public AiSettingsInfo Save(string provider, string model, string? newKey, bool removeKey)
    {
        if (!AiProviders.IsKnown(provider)) throw new ConnectionValidationException("Provedor de IA desconhecido.");
        model = model.Trim();
        if (model.Length == 0) model = AiProviders.DefaultModel(provider);
        if (!AiClient.IsValidModel(model)) throw new ConnectionValidationException("Nome de modelo inválido.");

        lock (_gate)
        {
            var f = Read();
            f.Provider = provider;
            f.Model = model;
            if (removeKey) f.KeyProtected = null;
            else if (!string.IsNullOrWhiteSpace(newKey)) f.KeyProtected = protector.Protect(newKey.Trim());

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(f));
            File.Move(tmp, path, overwrite: true);
            return new AiSettingsInfo(f.Provider, f.Model, !string.IsNullOrEmpty(f.KeyProtected));
        }
    }

    private FileModel Read()
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path)) is { } f && AiProviders.IsKnown(f.Provider))
            {
                if (string.IsNullOrWhiteSpace(f.Model)) f.Model = AiProviders.DefaultModel(f.Provider);
                return f;
            }
        }
        catch (JsonException) { }
        return new FileModel();
    }

    private sealed class FileModel
    {
        public string Provider { get; set; } = AiProviders.Anthropic;
        public string Model { get; set; } = AiProviders.DefaultModel(AiProviders.Anthropic);
        public string? KeyProtected { get; set; }
    }
}
