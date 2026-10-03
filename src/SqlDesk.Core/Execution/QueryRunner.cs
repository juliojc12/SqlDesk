using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using SqlDesk.Core.Providers;
using SqlDesk.Core.Sessions;
using SqlDesk.SqlAnalysis;

namespace SqlDesk.Core.Execution;

public sealed class TabBusyException(string message) : Exception(message);

public sealed class TabNotConnectedException(string message) : Exception(message);

/// <summary>
/// Executa batches já analisados na conexão da aba, um comando por vez por aba. Não decide nada sobre
/// segurança: quem chama passa o texto por <c>SqlScriptAnalyzer.Analyze</c> antes.
/// </summary>
public sealed class QueryRunner(TabSessionManager sessions) : IBatchRunner
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public bool IsRunning(string tabId) => _running.ContainsKey(tabId);

    /// <summary>Pede o cancelamento da execução da aba. Sem execução em andamento, não faz nada.</summary>
    public void Cancel(string tabId)
    {
        if (_running.TryGetValue(tabId, out var cts))
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* terminou no mesmo instante */ }
        }
    }

    /// <summary>
    /// Executa os batches em ordem e para no primeiro erro. <paramref name="baseOffset"/> e <paramref name="baseLine"/>
    /// situam o texto analisado dentro do documento (offset base 0 e linha base 1 onde o texto começa), para que
    /// resultados e erros venham em coordenadas do documento.
    /// </summary>
    public async Task<RunSummary> RunAsync(
        string tabId, IReadOnlyList<Batch> batches, int baseOffset, int baseLine, int? maxRows,
        IExecutionSink sink, CancellationToken externalCt = default)
    {
        var conn = sessions.GetConnection(tabId)
            ?? throw new TabNotConnectedException("A aba não está conectada. Conecte antes de executar.");
        var provider = sessions.GetProvider(tabId)
            ?? throw new TabNotConnectedException("A aba não está conectada. Conecte antes de executar.");
        var timeout = sessions.GetCommandTimeout(tabId) ?? 30;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        if (!_running.TryAdd(tabId, cts))
            throw new TabBusyException("Esta aba já está executando um comando.");

        var watch = Stopwatch.StartNew();
        long totalRows = 0;
        var resultIndex = 0;
        var status = RunStatus.Completed;
        int? errorNumber = null;

        IDisposable? infoSub = null;
        try
        {
            // Dentro do try: se a inscrição falhar, a aba não fica marcada como "executando" para sempre.
            infoSub = provider.SubscribeInfoMessages(conn, msg => sink.Message(MessageKinds.Info, msg, null));
            foreach (var batch in batches)
            {
                if (string.IsNullOrWhiteSpace(batch.Text)) continue;
                var source = new DocRange(baseOffset + batch.Start, batch.Text.Length);
                var batchFirstLine = baseLine - 1 + batch.StartLine;

                try
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = batch.Text;
                    cmd.CommandTimeout = timeout;
                    void Completed(long n)
                    {
                        sink.Message(MessageKinds.Rows, $"({n} {(n == 1 ? "linha afetada" : "linhas afetadas")})", null);
                        sink.StatementCompleted(n);
                    }
                    var driverReports = provider.TryAttachStatementCompleted(cmd, Completed);
                    var lastAffected = 0L;

                    await using var reader = await cmd.ExecuteReaderAsync(cts.Token);
                    do
                    {
                        var (emitted, count, _) = await ResultStreamer.StreamCurrentAsync(reader, resultIndex, source, sink, maxRows, cts.Token);
                        if (emitted)
                        {
                            resultIndex++;
                            totalRows += count;
                        }
                        if (!driverReports)
                        {
                            // Sem evento do driver (MySQL): result set devolve count linhas; comando sem result set usa RecordsAffected (acumulado no batch).
                            var affected = emitted ? count : Math.Max(0, reader.RecordsAffected - lastAffected);
                            if (!emitted) lastAffected = Math.Max(lastAffected, reader.RecordsAffected);
                            Completed(affected);
                        }
                    }
                    while (await reader.NextResultAsync(cts.Token));
                }
                catch (Exception ex) when (cts.IsCancellationRequested && ex is OperationCanceledException or DbException or InvalidOperationException)
                {
                    status = RunStatus.Cancelled;
                    sink.Message(MessageKinds.Error, "Execução cancelada pelo usuário.", null);
                    break;
                }
                catch (DbException ex)
                {
                    status = RunStatus.Error;
                    errorNumber = provider.ErrorNumber(ex);
                    var any = false;
                    foreach (var (message, line, isError) in provider.ErrorDetails(ex, batchFirstLine))
                    {
                        any = true;
                        sink.Message(isError ? MessageKinds.Error : MessageKinds.Info, message, line);
                    }
                    // O provedor não soube detalhar (outro tipo de DbException): o erro nunca passa em silêncio.
                    if (!any) sink.Message(MessageKinds.Error, ex.Message, null);
                    break;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    status = RunStatus.Error;
                    sink.Message(MessageKinds.Error, $"A execução foi interrompida: {ex.Message}", null);
                    break;
                }
            }

            // O MySQL atende o KILL QUERY sem erro quando o comando é interrompível (SELECT SLEEP devolve 1), e o pedido
            // pode chegar quando o último comando já acabou: o leitor termina "com sucesso". O pedido do usuário foi
            // cancelar, então o resultado não vale como completo, mas os comandos rodaram até o fim (e o que gravaram
            // ficou): a mensagem não pode dizer que nada aconteceu, senão convida a rodar de novo (ex.: x = x + 1).
            if (status == RunStatus.Completed && cts.IsCancellationRequested)
            {
                status = RunStatus.Cancelled;
                sink.Message(MessageKinds.Error,
                    "Cancelamento pedido, mas o comando já tinha terminado: confira os dados (as alterações podem ter sido gravadas).", null);
            }
        }
        finally
        {
            infoSub?.Dispose();
            _running.TryRemove(tabId, out _);
        }

        watch.Stop();
        sink.Message(MessageKinds.Timing, $"Tempo de execução: {FormatElapsed(watch.Elapsed)}", null);
        return new RunSummary(status, watch.ElapsedMilliseconds, totalRows, errorNumber);
    }

    public static string FormatElapsed(TimeSpan t) =>
        t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0} ms" :
        t.TotalMinutes < 1 ? $"{t.TotalSeconds:0.00} s" :
        $"{(int)t.TotalMinutes} min {t.Seconds} s";
}
