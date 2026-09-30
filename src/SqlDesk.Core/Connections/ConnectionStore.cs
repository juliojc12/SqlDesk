using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlDesk.Core.Connections;

/// <summary>Persiste as conexões em JSON. A senha é gravada protegida (DPAPI) e nunca sai do backend.</summary>
public sealed partial class ConnectionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly IPasswordProtector _protector;
    private readonly object _gate = new();

    public ConnectionStore(string path, IPasswordProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlDesk", "connections.json");

    public IReadOnlyList<ConnectionInfo> List()
    {
        lock (_gate) return Load().Select(ToInfo).ToList();
    }

    public ConnectionInfo? Get(Guid id)
    {
        lock (_gate) return Load().Where(e => e.Id == id).Select(ToInfo).FirstOrDefault();
    }

    /// <summary>Senha em texto puro, apenas para uso interno do Core (abrir conexões).</summary>
    public string? GetPassword(Guid id)
    {
        lock (_gate)
        {
            var e = Load().FirstOrDefault(x => x.Id == id);
            return e?.PasswordProtected is { Length: > 0 } p ? _protector.Unprotect(p) : null;
        }
    }

    /// <summary>Cria ou atualiza. <c>Password</c> nulo/vazio mantém a senha salva.</summary>
    public ConnectionInfo Save(SaveConnectionRequest req)
    {
        Validate(req);
        lock (_gate)
        {
            var all = Load();
            var existing = req.Id is { } id ? all.FirstOrDefault(e => e.Id == id) : null;
            if (req.Id is not null && existing is null)
                throw new ConnectionValidationException("Conexão não encontrada.");

            var protectedPwd = !string.IsNullOrEmpty(req.Password) ? _protector.Protect(req.Password) : existing?.PasswordProtected;
            var entry = new Entry
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                Name = req.Name.Trim(),
                Color = req.Color,
                Settings = req.Settings,
                PasswordProtected = protectedPwd,
            };
            if (existing is null) all.Add(entry); else all[all.IndexOf(existing)] = entry;
            Persist(all);
            return ToInfo(entry);
        }
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            var all = Load();
            var removed = all.RemoveAll(e => e.Id == id) > 0;
            if (removed) Persist(all);
            return removed;
        }
    }

    public ConnectionInfo Duplicate(Guid id)
    {
        lock (_gate)
        {
            var all = Load();
            var src = all.FirstOrDefault(e => e.Id == id) ?? throw new ConnectionValidationException("Conexão não encontrada.");
            var copy = new Entry
            {
                Id = Guid.NewGuid(),
                Name = UniqueName(all, src.Name + " (cópia)"),
                Color = src.Color,
                Settings = src.Settings,
                PasswordProtected = src.PasswordProtected,
            };
            all.Insert(all.IndexOf(src) + 1, copy);
            Persist(all);
            return ToInfo(copy);
        }
    }

    private static string UniqueName(List<Entry> all, string baseName)
    {
        var name = baseName;
        for (var i = 2; all.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = $"{baseName} {i}";
        return name;
    }

    private static void Validate(SaveConnectionRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new ConnectionValidationException("Informe um nome para a conexão.");
        if (string.IsNullOrWhiteSpace(r.Settings.Server)) throw new ConnectionValidationException("Informe o servidor.");
        if (!HexColor().IsMatch(r.Color ?? "")) throw new ConnectionValidationException("Cor inválida (use #RRGGBB).");
        if (r.Settings.ConnectTimeout < 0 || r.Settings.CommandTimeout < 0) throw new ConnectionValidationException("Timeouts não podem ser negativos.");
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    private static ConnectionInfo ToInfo(Entry e) =>
        new(e.Id, e.Name, e.Color, e.Settings, !string.IsNullOrEmpty(e.PasswordProtected));

    private List<Entry> Load()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            using var fs = File.OpenRead(_path);
            return JsonSerializer.Deserialize<FileModel>(fs, Json)?.Connections ?? [];
        }
        catch (JsonException ex)
        {
            // Não sobrescreve um arquivo ilegível: o usuário pode recuperá-lo manualmente.
            throw new InvalidDataException($"O arquivo de conexões está corrompido ({_path}): {ex.Message}", ex);
        }
    }

    private void Persist(List<Entry> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel { Connections = all }, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private sealed class FileModel
    {
        public int Version { get; set; } = 1;

        public List<Entry> Connections { get; set; } = [];
    }

    private sealed class Entry
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public string Color { get; set; } = "#0078D4";

        public ConnectionSettings Settings { get; set; } = new("", "", "");

        public string? PasswordProtected { get; set; }
    }
}
