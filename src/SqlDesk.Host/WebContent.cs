using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SqlDesk.Host;

/// <summary>
/// Em Release, o frontend vai embutido como recursos "web/...". Extrai para uma pasta versionada
/// em %LOCALAPPDATA% (o virtual host do WebView2 precisa de uma pasta em disco).
/// </summary>
public static class WebContent
{
    public const string VirtualHost = "app.sqldesk";
    private const string Prefix = "web/";

    public static string Extract()
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("Frontend não embutido neste build (use Release ou o servidor do Vite).");

        var seed = string.Join('|', names) + asm.ManifestModule.ModuleVersionId;
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..12];
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlDesk", "web", version);
        var marker = Path.Combine(root, ".complete");
        if (File.Exists(marker)) return root;

        Directory.CreateDirectory(root);
        foreach (var name in names)
        {
            var target = Path.Combine(root, name[Prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = asm.GetManifestResourceStream(name)!;
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }
        File.WriteAllText(marker, "");
        return root;
    }
}
