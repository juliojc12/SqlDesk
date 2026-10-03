using System.Collections.Concurrent;
using System.Data.Common;
using SqlDesk.Core.Connections;
using SqlDesk.Core.Providers;

namespace SqlDesk.Core.Sessions;

public sealed class PasswordRequiredException(string message) : Exception(message);

public sealed class ConnectFailedException(string message, Exception inner, bool certificateUntrusted = false) : Exception(message, inner)
{
    public bool CertificateUntrusted { get; } = certificateUntrusted;
}

public sealed record OpenSessionResult(string ServerVersion, string Database);

/// <summary>
/// Uma <see cref="DbConnection"/> por aba de query, mantida aberta enquanto a aba existir
/// (necessário para que uma transação iniciada numa aba continue valendo entre execuções).
/// </summary>
public sealed class TabSessionManager : IAsyncDisposable
{
    private sealed record Session(Guid ConnectionId, DbConnection Connection, int CommandTimeout, IDatabaseProvider Provider);

    private readonly ConnectionStore _store;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<Guid, string> _runtimePasswords = new();
    private readonly ConcurrentDictionary<string, int> _trackedTransactions = new();

    public TabSessionManager(ConnectionStore store) => _store = store;

    /// <summary>A conexão da aba foi fechada (o servidor desfaz qualquer transação aberta nela).</summary>
    public event Action<string>? TabDisconnected;

    public bool IsConnected(string tabId) => _sessions.ContainsKey(tabId);

    public DbConnection? GetConnection(string tabId) => _sessions.TryGetValue(tabId, out var s) ? s.Connection : null;

    /// <summary>Provedor do banco da conexão aberta na aba, ou null se a aba não está conectada.</summary>
    public IDatabaseProvider? GetProvider(string tabId) => _sessions.TryGetValue(tabId, out var s) ? s.Provider : null;

    public int? GetCommandTimeout(string tabId) => _sessions.TryGetValue(tabId, out var s) ? s.CommandTimeout : null;

    /// <summary>Transações que o próprio app abriu na aba e ainda não confirmou nem desfez (zera ao desconectar).</summary>
    public int TrackedTransactionCount(string tabId) => _trackedTransactions.TryGetValue(tabId, out var n) ? n : 0;

    public void SetTrackedTransactions(string tabId, int count)
    {
        if (count > 0) _trackedTransactions[tabId] = count;
        else _trackedTransactions.TryRemove(tabId, out _);
    }

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

        var provider = ProviderRegistry.For(info.Settings);
        var conn = provider.CreateConnection(provider.BuildConnectionString(info.Settings, effective));
        try
        {
            await conn.OpenAsync(ct);
        }
        catch (DbException ex)
        {
            await conn.DisposeAsync();
            var f = provider.Translate(ex);
            throw new ConnectFailedException(f.Message, ex, f.CertificateUntrusted);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }

        if (password is not null && !info.HasPassword) _runtimePasswords[connectionId] = password;
        _sessions[tabId] = new Session(connectionId, conn, info.Settings.CommandTimeout, provider);
        return new OpenSessionResult(conn.ServerVersion, conn.Database);
    }

    /// <summary>Registra uma sessão pronta (conexão e provedor falsos), para testar quem usa a sessão sem um banco de verdade.</summary>
    internal void AttachForTests(string tabId, DbConnection connection, IDatabaseProvider provider, int commandTimeout = 30) =>
        _sessions[tabId] = new Session(Guid.NewGuid(), connection, commandTimeout, provider);

    /// <summary>
    /// Abre uma conexão própria (fora das abas) para trabalho em segundo plano, como carregar metadados, sem disputar a
    /// conexão de uma aba em execução. Usa só a senha salva ou a já informada nesta execução do app; nunca pede nem inventa.
    /// </summary>
    public async Task<(DbConnection Connection, IDatabaseProvider Provider)> OpenSideConnectionAsync(Guid connectionId, CancellationToken ct = default)
    {
        var info = _store.Get(connectionId) ?? throw new ConnectionValidationException("Conexão não encontrada.");
        var password = _store.GetPassword(connectionId);
        if (password is null && _runtimePasswords.TryGetValue(connectionId, out var cached)) password = cached;
        if (password is null)
            throw new PasswordRequiredException($"A conexão '{info.Name}' não tem senha salva. Conecte uma aba informando a senha.");

        var provider = ProviderRegistry.For(info.Settings);
        var conn = provider.CreateConnection(provider.BuildConnectionString(info.Settings, password));
        try
        {
            await conn.OpenAsync(ct);
            return (conn, provider);
        }
        catch (DbException ex)
        {
            await conn.DisposeAsync();
            var f = provider.Translate(ex);
            throw new ConnectFailedException(f.Message, ex, f.CertificateUntrusted);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>Fecha a conexão mas mantém a aba (o texto não se perde).</summary>
    public async Task DisconnectAsync(string tabId)
    {
        // Conexão fechada (ou trocada): o servidor desfaz o que estava aberto nela.
        _trackedTransactions.TryRemove(tabId, out _);
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
