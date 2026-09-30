namespace SqlDesk.Core.Connections;

public sealed record ConnectionSettings(
    string Server,
    string Database,
    string User,
    int ConnectTimeout = 15,
    int CommandTimeout = 30,
    bool Encrypt = true,
    bool TrustServerCertificate = false,
    IReadOnlyDictionary<string, string>? Advanced = null)
{
    public IReadOnlyDictionary<string, string> Advanced { get; init; } = Advanced ?? new Dictionary<string, string>();
}

/// <summary>Conexão como vista pelo frontend: a senha nunca sai do backend, só o indicador <see cref="HasPassword"/>.</summary>
public sealed record ConnectionInfo(Guid Id, string Name, string Color, ConnectionSettings Settings, bool HasPassword);

public sealed record SaveConnectionRequest(
    Guid? Id,
    string Name,
    string Color,
    ConnectionSettings Settings,
    string? Password);

public sealed record TestConnectionResult(bool Ok, string? ServerVersion, string? ErrorMessage, bool CertificateUntrusted = false);

public sealed class ConnectionValidationException(string message) : Exception(message);
