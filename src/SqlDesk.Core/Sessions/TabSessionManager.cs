using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using SqlDesk.Core.Connections;

namespace SqlDesk.Core.Sessions;

public sealed class PasswordRequiredException(string message) : Exception(message);

public sealed class ConnectFailedException(string message, Exception inner, bool certificateUntrusted = false) : Exception(message, inner)
{
    public bool CertificateUntrusted { get; } = certificateUntrusted;
}

public sealed record OpenSessionResult(string ServerVersion, string Database);

/// <summary>
/// Uma <see cref="SqlConnection"/> por aba de query, mantida aberta enquanto a aba existir
/// (necessário para que uma transação iniciada numa aba continue valendo entre execuções).
/// </summary>
public sealed class TabSessionManager : IAsyncDisposable
{
    private sealed record Session(Guid ConnectionId, SqlConnection Connection, int CommandTimeout);

    private readonly ConnectionStore _store;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<Guid, string> _runtimePasswords = new();

    public TabSessionManager(ConnectionStore store) => _store = store;

    /// <summary>A conexão da aba foi fechada (o servidor desfaz qualquer transação aberta nela).</summary>
    public event Action<string>? TabDisconnected;

    public bool IsConnected(string tabId) => _sessions.ContainsKey(tabId);

    public SqlConnection? GetConnection(string tabId) => _sessions.TryGetValue(tabId, out var s) ? s.Connection : null;

    public int? GetCommandTimeout(string tabId) => _sessions.TryGetValue(tabId, out var s) ? s.CommandTimeout : null;

    /// <summary>
    /// Abre (ou reabre) a sessão da aba. Sem senha salva e sem <paramref name="password"/>, lança
    /// <see cref="PasswordRequiredException"/>: nunca tenta conectar com senha vazia por conta própria.
    /// </summary>
    public async Task<OpenSessionResult> OpenAsync(string tabId, Guid connectionId, string? password, CancellationToken ct = default)
    {
        var info = _store.Get(connectionId) ?? throw new ConnectionValidationException("Conexão não encontrada.");

        var effective = password ?? _store.GetPassword(connectionId);
        if (effective is null && _runtimePasswords.TryGetValue(connectionId, out var cached)) effective = cached;
        if (effective is null)
            throw new PasswordRequiredException($"A conexão '{info.Name}' não tem senha salva. Informe a senha para conectar.");

        await DisconnectAsync(tabId);

        var conn = new SqlConnection(ConnectionStringService.Build(info.Settings, effective));
        try
        {
            await conn.OpenAsync(ct);
        }
        catch (SqlException ex)
        {
            await conn.DisposeAsync();
            throw new ConnectFailedException(SqlErrorTranslator.Translate(ex), ex, SqlErrorTranslator.IsCertificateError(ex));
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }

        if (password is not null && !info.HasPassword) _runtimePasswords[connectionId] = password;
        _sessions[tabId] = new Session(connectionId, conn, info.Settings.CommandTimeout);
        return new OpenSessionResult(conn.ServerVersion, conn.Database);
    }

    /// <summary>Fecha a conexão mas mantém a aba (o texto não se perde).</summary>
    public async Task DisconnectAsync(string tabId)
    {
        if (_sessions.TryRemove(tabId, out var s))
        {
            TabDisconnected?.Invoke(tabId);
            await s.Connection.DisposeAsync();
        }
    }

    public async Task DisconnectConnectionAsync(Guid connectionId)
    {
        foreach (var (tabId, s) in _sessions.ToArray())
            if (s.ConnectionId == connectionId) await DisconnectAsync(tabId);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var tabId in _sessions.Keys.ToArray()) await DisconnectAsync(tabId);
    }
}
