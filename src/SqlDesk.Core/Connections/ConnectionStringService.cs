using Microsoft.Data.SqlClient;

namespace SqlDesk.Core.Connections;

/// <summary>Converte entre campos e connection string usando <see cref="SqlConnectionStringBuilder"/>. Só autenticação SQL.</summary>
public static class ConnectionStringService
{
    private static readonly string[] ServerKeys = ["data source", "server", "address", "addr", "network address"];
    private static readonly string[] DatabaseKeys = ["initial catalog", "database"];
    private static readonly string[] UserKeys = ["user id", "uid", "user"];
    private static readonly string[] PasswordKeys = ["password", "pwd"];
    private static readonly string[] ConnectTimeoutKeys = ["connect timeout", "connection timeout", "timeout"];
    private static readonly string[] EncryptKeys = ["encrypt"];
    private static readonly string[] TrustKeys = ["trustservercertificate", "trust server certificate"];
    private static readonly string[] CommandTimeoutKeys = ["command timeout"];

    /// <summary>Gera a connection string. <paramref name="password"/> vazio/nulo omite a chave Password.</summary>
    public static string Build(ConnectionSettings s, string? password)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = s.Server,
            InitialCatalog = s.Database,
            UserID = s.User,
            ConnectTimeout = s.ConnectTimeout,
            Encrypt = s.Encrypt,
            TrustServerCertificate = s.TrustServerCertificate,
        };
        if (!string.IsNullOrEmpty(password)) b.Password = password;
        foreach (var (key, value) in s.Advanced) b[key] = value;
        return b.ConnectionString;
    }

    public static (ConnectionSettings Settings, string? Password) Parse(string connectionString)
    {
        var raw = new System.Data.Common.DbConnectionStringBuilder();
        try { raw.ConnectionString = connectionString; }
        catch (ArgumentException ex) { throw new ConnectionValidationException("Connection string inválida: " + ex.Message); }

        string server = "", database = "", user = "";
        string? password = null;
        int connectTimeout = 15, commandTimeout = 30;
        bool encrypt = true, trust = false;
        var advanced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string key in raw.Keys)
        {
            var value = Convert.ToString(raw[key]) ?? "";
            var k = key.Trim().ToLowerInvariant();
            if (ServerKeys.Contains(k)) server = value;
            else if (DatabaseKeys.Contains(k)) database = value;
            else if (UserKeys.Contains(k)) user = value;
            else if (PasswordKeys.Contains(k)) password = value;
            else if (ConnectTimeoutKeys.Contains(k)) connectTimeout = ParseInt(key, value);
            else if (CommandTimeoutKeys.Contains(k)) commandTimeout = ParseInt(key, value);
            else if (EncryptKeys.Contains(k))
            {
                if (value.Equals("strict", StringComparison.OrdinalIgnoreCase)) advanced["Encrypt"] = "Strict";
                else encrypt = ParseBool(key, value);
            }
            else if (TrustKeys.Contains(k)) trust = ParseBool(key, value);
            else if (k is "integrated security" or "trusted_connection")
            {
                if (ParseBool(key, value) || value.Equals("sspi", StringComparison.OrdinalIgnoreCase)) throw OutOfScope("Windows Authentication");
            }
            else if (k == "authentication")
            {
                var v = value.Replace(" ", "").ToLowerInvariant();
                if (v is not ("sqlpassword" or "notspecified" or "")) throw OutOfScope("Authentication=" + value);
            }
            else advanced[CanonicalKey(key, value)] = value;
        }

        return (new ConnectionSettings(server, database, user, connectTimeout, commandTimeout, encrypt, trust, advanced), password);
    }

    /// <summary>Nome canônico da palavra-chave (ex.: "application name" vira "Application Name"); rejeita chaves que o SqlClient não conhece.</summary>
    private static string CanonicalKey(string key, string value)
    {
        try
        {
            var probe = new SqlConnectionStringBuilder { [key] = value };
            var text = probe.ConnectionString;
            return text[..text.IndexOf('=')];
        }
        catch (ArgumentException)
        {
            throw new ConnectionValidationException($"Palavra-chave não suportada: '{key}'.");
        }
    }

    private static ConnectionValidationException OutOfScope(string what) =>
        new($"{what} está fora do escopo: só autenticação SQL (usuário e senha) é suportada.");

    private static int ParseInt(string key, string value) =>
        int.TryParse(value, out var n) && n >= 0 ? n : throw new ConnectionValidationException($"Valor inválido para '{key}': '{value}'.");

    private static bool ParseBool(string key, string value) => value.ToLowerInvariant() switch
    {
        "true" or "yes" or "1" or "mandatory" => true,
        "false" or "no" or "0" or "optional" => false,
        _ => throw new ConnectionValidationException($"Valor inválido para '{key}': '{value}'."),
    };
}
