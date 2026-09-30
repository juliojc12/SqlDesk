namespace SqlDesk.Core.Sessions;

/// <summary>Guarda o estado das abas (JSON opaco montado pelo frontend) para restaurar ao reabrir o app.</summary>
public sealed class SessionStateStore(string path)
{
    private readonly object _gate = new();

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlDesk", "session.json");

    public string? Load()
    {
        lock (_gate) return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Save(string json)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
