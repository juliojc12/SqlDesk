namespace SqlDesk.Core.Execution;

/// <summary>Categoria de coluna usada pelo frontend para ordenar e alinhar: number, text, date, bool ou binary.</summary>
public sealed record ColumnInfo(string Name, string Kind, string TypeName);

public static class MessageKinds
{
    public const string Info = "info";
    public const string Rows = "rows";
    public const string Error = "error";
    public const string Timing = "timing";
}

public static class RunStatus
{
    public const string Completed = "completed";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
}

/// <summary>Intervalo no documento original (offset base 0 e comprimento em caracteres).</summary>
public sealed record DocRange(int Start, int Length);

/// <summary>
/// Destino dos eventos de uma execução. As chamadas vêm da thread que lê o <c>SqlDataReader</c>, na ordem em que
/// os fatos acontecem; a implementação precisa ser segura para uso a partir de qualquer thread.
/// </summary>
public interface IExecutionSink
{
    /// <param name="resultIndex">Índice do result set na execução (0, 1, 2...), contínuo entre os batches.</param>
    /// <param name="source">Trecho do documento que gerou o result set (o batch).</param>
    void ResultStarted(int resultIndex, DocRange source, IReadOnlyList<ColumnInfo> columns);

    void Rows(int resultIndex, IReadOnlyList<object?[]> rows);

    void ResultCompleted(int resultIndex, long rowCount, bool truncated);

    /// <param name="line">Linha (base 1) no documento, quando conhecida.</param>
    void Message(string kind, string text, int? line);

    /// <summary>Um statement terminou e afetou (ou devolveu) <paramref name="recordCount"/> linhas.</summary>
    void StatementCompleted(long recordCount) { }

    /// <summary>
    /// Se verdadeiro, as linhas chegam com o tipo original do .NET (decimal, DateTime, byte[]...) e sem o corte de texto e binário
    /// da grade, em vez de convertidas para JSON. Usado pela exportação.
    /// </summary>
    bool WantsRawValues => false;
}

/// <param name="ErrorNumber">Número do primeiro erro do servidor, quando a execução falhou por erro do banco (DbException).</param>
public sealed record RunSummary(string Status, long ElapsedMs, long TotalRows, int? ErrorNumber = null);

/// <summary>Executa batches já analisados na conexão da aba. Separado para que o fluxo com travas seja testável.</summary>
public interface IBatchRunner
{
    bool IsRunning(string tabId);

    Task<RunSummary> RunAsync(
        string tabId, IReadOnlyList<SqlDesk.SqlAnalysis.Batch> batches, int baseOffset, int baseLine, int? maxRows,
        IExecutionSink sink, CancellationToken externalCt = default);
}

/// <summary>Comandos fixos (nunca texto do usuário) que o controle de transações roda na conexão da aba.</summary>
public interface ISessionDb
{
    /// <summary>Provedor do banco da conexão da aba (de onde vêm os comandos de transação e a reescrita).</summary>
    SqlDesk.Core.Providers.IDatabaseProvider ProviderOf(string tabId);

    Task<int> TranCountAsync(string tabId, CancellationToken ct);

    Task ExecAsync(string tabId, string sql, CancellationToken ct);

    Task<long?> CountAsync(string tabId, string sql, CancellationToken ct);

    /// <summary>
    /// Registra quantas transações o próprio app deixou abertas na aba (1 depois de abrir, 0 depois de confirmar ou desfazer).
    /// Serve de resposta quando o banco não deixa consultar o estado da transação.
    /// </summary>
    void SetTrackedTransactions(string tabId, int count) { }

    /// <summary>
    /// Dos <paramref name="targets"/> (nomes como o analisador os devolve: <c>t</c>, <c>db.t</c>, <c>`db`.`t`</c>), os que
    /// não guardam alterações numa transação (MySQL/MariaDB: mecanismo diferente de InnoDB, como MyISAM, Aria, MEMORY,
    /// ou view). Vazio = todos transacionais (ou o banco não tem essa diferença, como o SQL Server). null = não foi
    /// possível conferir (nome não reconhecido, tabela não encontrada): quem chama assume o pior.
    /// </summary>
    Task<IReadOnlyList<string>?> NonTransactionalTablesAsync(string tabId, IReadOnlyList<string> targets, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>?>([]);

    /// <summary>
    /// Chamado logo depois de um ROLLBACK (ou ROLLBACK TO SAVEPOINT): true se o servidor avisou que alterações em tabelas
    /// não transacionais não foram desfeitas (MySQL/MariaDB: aviso 1196).
    /// </summary>
    Task<bool> RollbackLeftChangesAsync(string tabId, CancellationToken ct) => Task.FromResult(false);
}
