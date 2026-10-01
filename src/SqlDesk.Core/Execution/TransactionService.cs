using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using SqlDesk.Core.Sessions;

namespace SqlDesk.Core.Execution;

/// <summary>Comandos fixos de transação na conexão da aba, a partir do <see cref="TabSessionManager"/>.</summary>
public sealed class SqlSessionDb(TabSessionManager sessions) : ISessionDb
{
    private SqlConnection Conn(string tabId) =>
        sessions.GetConnection(tabId) ?? throw new TabNotConnectedException("A aba não está conectada.");

    public async Task<int> TranCountAsync(string tabId, CancellationToken ct)
    {
        await using var cmd = Conn(tabId).CreateCommand();
        cmd.CommandText = "SELECT @@TRANCOUNT";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task ExecAsync(string tabId, string sql, CancellationToken ct)
    {
        await using var cmd = Conn(tabId).CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = sessions.GetCommandTimeout(tabId) ?? 30;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long?> CountAsync(string tabId, string sql, CancellationToken ct)
    {
        await using var cmd = Conn(tabId).CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = sessions.GetCommandTimeout(tabId) ?? 30;
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToInt64(v);
    }
}

/// <summary>
/// Controla as transações abertas por aba: consulta <c>@@TRANCOUNT</c> depois de cada execução e guarda o último valor
/// conhecido (usado para avisar ao fechar aba ou app).
/// </summary>
public sealed class TransactionService(ISessionDb db)
{
    private readonly ConcurrentDictionary<string, int> _counts = new();

    public int Known(string tabId) => _counts.TryGetValue(tabId, out var n) ? n : 0;

    /// <summary>Abas com transação aberta, pelo último valor conhecido.</summary>
    public IReadOnlyDictionary<string, int> OpenTabs() => _counts.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);

    public void Forget(string tabId) => _counts.TryRemove(tabId, out _);

    public async Task<int> RefreshAsync(string tabId, CancellationToken ct = default)
    {
        var n = await db.TranCountAsync(tabId, ct);
        _counts[tabId] = n;
        return n;
    }

    public async Task<int> BeginAsync(string tabId, CancellationToken ct = default)
    {
        await db.ExecAsync(tabId, "BEGIN TRANSACTION", ct);
        return await RefreshAsync(tabId, ct);
    }

    /// <summary>Confirma tudo (inclusive transações aninhadas, que um COMMIT simples só decrementaria).</summary>
    public async Task<int> CommitAsync(string tabId, CancellationToken ct = default)
    {
        await db.ExecAsync(tabId, "WHILE @@TRANCOUNT > 0 COMMIT TRANSACTION", ct);
        return await RefreshAsync(tabId, ct);
    }

    public async Task<int> RollbackAsync(string tabId, CancellationToken ct = default)
    {
        await db.ExecAsync(tabId, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", ct);
        return await RefreshAsync(tabId, ct);
    }
}
