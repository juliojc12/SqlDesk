using System.Net.Sockets;
using SqlDesk.Core.Connections;
using SqlDesk.Core.Providers;

namespace SqlDesk.Integration.Tests;

/// <summary>
/// Servidores dos containers de teste (tests/docker). A senha vem só da variável de ambiente SQLDESK_TEST_PWD;
/// nunca é gravada em arquivo.
/// </summary>
public static class TestServers
{
    private static readonly Dictionary<string, int> Ports = new() { ["mysql"] = 33306, ["mariadb"] = 33307 };

    public static IEnumerable<object[]> All => Ports.Keys.Select(name => new object[] { name });

    public static string? Password => Environment.GetEnvironmentVariable("SQLDESK_TEST_PWD") is { Length: > 0 } p ? p : null;

    public static ConnectionSettings For(string name) =>
        new($"127.0.0.1:{Ports[name]}", "sqldesk_test", "sqldesk", Encrypt: false) { Provider = ProviderIds.MySql };

    /// <summary>Motivo para pular os testes de integração, ou null se dá para rodá-los.</summary>
    internal static string? SkipReason()
    {
        if (Password is null) return "SQLDESK_TEST_PWD não definida: testes de integração pulados.";
        foreach (var (name, port) in Ports)
            if (!PortOpen(port)) return $"Container '{name}' fora do ar (127.0.0.1:{port}): testes de integração pulados.";
        return null;
    }

    private static bool PortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    // A verificação roda uma vez por execução (cada atributo pergunta o mesmo).
    internal static readonly Lazy<string?> CachedSkipReason = new(SkipReason);
}

/// <summary>Fato que só roda com a senha definida e os containers de pé; senão fica como pulado (não falha).</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (TestServers.CachedSkipReason.Value is { } reason) Skip = reason;
    }
}

/// <summary>Teoria que só roda com a senha definida e os containers de pé; senão fica como pulada (não falha).</summary>
public sealed class IntegrationTheoryAttribute : TheoryAttribute
{
    public IntegrationTheoryAttribute()
    {
        if (TestServers.CachedSkipReason.Value is { } reason) Skip = reason;
    }
}
