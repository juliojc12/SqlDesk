using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using MySqlConnector;

namespace SqlDesk.Core.Providers;

/// <summary>
/// Descobre se a conexão MySQL/MariaDB está dentro de uma transação (o MySQL não tem <c>@@TRANCOUNT</c>). Responde 1 ou 0,
/// ou null quando não consegue saber; quem chama decide o que fazer com o null (o app usa o estado que ele mesmo rastreou).
/// </summary>
/// <remarks>
/// Estratégias, nesta ordem (a primeira que funcionar fica memorizada para a conexão):
/// <list type="number">
/// <item><c>SELECT @@in_transaction</c>: só existe no MariaDB; leitura pura, sem privilégio, vale 1 logo depois do
/// <c>START TRANSACTION</c>. No MySQL dá erro 1193 (variável desconhecida).</item>
/// <item>Savepoint de sonda: <c>SAVEPOINT</c> seguido de <c>RELEASE SAVEPOINT</c>. Dentro de uma transação os dois
/// funcionam e nada muda (o savepoint some no RELEASE); fora dela, com autocommit ligado, o servidor não guarda o
/// savepoint e o RELEASE falha com 1305 ("does not exist"). Funciona para qualquer usuário e reflete o estado real do
/// servidor, inclusive o <c>START TRANSACTION</c>/<c>COMMIT</c> escrito no próprio script.</item>
/// </list>
/// O <c>performance_schema.events_transactions_current</c> foi descartado: no MySQL 8.4 um usuário comum (só com
/// privilégios no próprio banco) recebe erro 1142 ao consultá-lo, e com o performance_schema ou o consumidor de
/// transações desligados a consulta devolve 0 em silêncio, o que seria "não há transação" sem ter certeza.
/// Com <c>autocommit = 0</c>, o savepoint deixa a sessão "dentro de transação" (como qualquer instrução faria nesse modo)
/// e a resposta é 1: erra para o lado de avisar.
/// </remarks>
public static class MySqlTransactionProbe
{
    private enum Strategy { Unknown, InTransactionVariable, Savepoint }

    private sealed class StrategyBox { public Strategy Strategy; }

    private const int SavepointDoesNotExist = 1305;

    private const string ProbeSavepoint = "sqldesk_tx_probe";

    private static readonly ConditionalWeakTable<DbConnection, StrategyBox> Strategies = new();

    public static async Task<int?> OpenCountAsync(DbConnection conn, CancellationToken ct)
    {
        var box = Strategies.GetOrCreateValue(conn);
        switch (box.Strategy)
        {
            case Strategy.InTransactionVariable: return await InTransactionVariableAsync(conn, ct);
            case Strategy.Savepoint: return await SavepointAsync(conn, ct);
        }

        try
        {
            var n = await InTransactionVariableAsync(conn, ct);
            box.Strategy = Strategy.InTransactionVariable;
            return n;
        }
        catch (DbException) when (conn.State == ConnectionState.Open) { /* MySQL: tenta a próxima */ }

        var r = await SavepointAsync(conn, ct);
        if (r is not null) box.Strategy = Strategy.Savepoint;
        return r;
    }

    private static async Task<int> InTransactionVariableAsync(DbConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT @@in_transaction";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0 ? 1 : 0;
    }

    private static async Task<int?> SavepointAsync(DbConnection conn, CancellationToken ct)
    {
        try
        {
            await ExecAsync(conn, "SAVEPOINT " + ProbeSavepoint, ct);
        }
        catch (DbException) when (conn.State == ConnectionState.Open)
        {
            return null; // não deu para perguntar: quem chama usa o estado rastreado
        }

        try
        {
            await ExecAsync(conn, "RELEASE SAVEPOINT " + ProbeSavepoint, ct);
            return 1;
        }
        catch (MySqlException ex) when (ex.Number == SavepointDoesNotExist)
        {
            return 0;
        }
        catch (DbException) when (conn.State == ConnectionState.Open)
        {
            return null;
        }
    }

    private static async Task ExecAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
