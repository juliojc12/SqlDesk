using System.Collections.Concurrent;
using System.Data.Common;
using SqlDesk.Core.Providers;
using SqlDesk.Core.Sessions;

namespace SqlDesk.Core.Execution;

/// <summary>Comandos fixos de transação na conexão da aba, a partir do <see cref="TabSessionManager"/>.</summary>
public sealed class SessionDb : ISessionDb
{
    private readonly TabSessionManager sessions;
    private readonly Func<DbConnection, CancellationToken, Task<int?>> probe;

    public SessionDb(TabSessionManager sessions) : this(sessions, MySqlTransactionProbe.OpenCountAsync) { }

    /// <summary>Para testes: troca a sonda de transação dos bancos sem <see cref="ITransactionSql.OpenCountSql"/>.</summary>
    internal SessionDb(TabSessionManager sessions, Func<DbConnection, CancellationToken, Task<int?>> probe)
    {
        this.sessions = sessions;
        this.probe = probe;
    }

    private DbConnection Conn(string tabId) =>
        sessions.GetConnection(tabId) ?? throw new TabNotConnectedException("A aba não está conectada.");

    public IDatabaseProvider ProviderOf(string tabId) =>
        sessions.GetProvider(tabId) ?? throw new TabNotConnectedException("A aba não está conectada.");

    public async Task<int> TranCountAsync(string tabId, CancellationToken ct)
    {
        if (ProviderOf(tabId).Transactions.OpenCountSql is { } sql)
        {
            await using var cmd = Conn(tabId).CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        // MySQL/MariaDB: a sonda pergunta ao servidor (vê também o START TRANSACTION/COMMIT do próprio script). Se não
        // conseguir, vale o que o app abriu e ainda não fechou: na dúvida, "há transação" (avisa ao fechar a aba), nunca 0.
        return await probe(Conn(tabId), ct) ?? sessions.TrackedTransactionCount(tabId);
    }

    public void SetTrackedTransactions(string tabId, int count) => sessions.SetTrackedTransactions(tabId, count);

    public async Task ExecAsync(string tabId, string sql, CancellationToken ct)
    {
        await using var cmd = Conn(tabId).CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = sessions.GetCommandTimeout(tabId) ?? 30;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>?> NonTransactionalTablesAsync(string tabId, IReadOnlyList<string> targets, CancellationToken ct)
    {
        var tx = ProviderOf(tabId).Transactions;
        if (tx.TableEngineSql is not { } sql) return [];
        var result = new List<string>();
        foreach (var target in targets)
        {
            if (!MySqlProvider.TrySplitTableName(target, out var schema, out var name)) return null;
            await using var cmd = Conn(tabId).CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = sessions.GetCommandTimeout(tabId) ?? 30;
            AddParameter(cmd, "@schema", schema);
            AddParameter(cmd, "@name", name);
            var found = false;
            var transactional = true;
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                // Com nomes que só diferem em maiúsculas pode vir mais de uma linha: todas precisam ser transacionais.
                while (await reader.ReadAsync(ct))
                {
                    found = true;
                    transactional &= tx.IsTransactionalEngine(reader.IsDBNull(2) ? null : reader.GetString(2));
                }
            }
            // Não encontrada (tabela temporária, nome errado, sem permissão de ver): não dá para garantir nada.
            if (!found) return null;
            if (!transactional) result.Add(target);
        }
        return result;
    }

    private static void AddParameter(DbCommand cmd, string name, string? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = (object?)value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    public async Task<bool> RollbackLeftChangesAsync(string tabId, CancellationToken ct)
    {
        if (ProviderOf(tabId).Transactions.RollbackIncompleteWarning is not { } code) return false;
        await using var cmd = Conn(tabId).CreateCommand();
        cmd.CommandText = "SHOW WARNINGS";
        cmd.CommandTimeout = sessions.GetCommandTimeout(tabId) ?? 30;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (Convert.ToInt32(reader.GetValue(1)) == code) return true;
        return false;
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
        await db.ExecAsync(tabId, db.ProviderOf(tabId).Transactions.BeginSql, ct);
        db.SetTrackedTransactions(tabId, 1);
        return await RefreshAsync(tabId, ct);
    }

    /// <summary>Confirma tudo (inclusive transações aninhadas, que um COMMIT simples só decrementaria).</summary>
    public async Task<int> CommitAsync(string tabId, CancellationToken ct = default)
    {
        await db.ExecAsync(tabId, db.ProviderOf(tabId).Transactions.CommitAllSql, ct);
        db.SetTrackedTransactions(tabId, 0);
        return await RefreshAsync(tabId, ct);
    }

    public async Task<int> RollbackAsync(string tabId, CancellationToken ct = default)
    {
        await db.ExecAsync(tabId, db.ProviderOf(tabId).Transactions.RollbackAllSql, ct);
        db.SetTrackedTransactions(tabId, 0);
        return await RefreshAsync(tabId, ct);
    }
}
