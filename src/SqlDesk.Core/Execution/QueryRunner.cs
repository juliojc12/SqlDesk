using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
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
        var timeout = sessions.GetCommandTimeout(tabId) ?? 30;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        if (!_running.TryAdd(tabId, cts))
            throw new TabBusyException("Esta aba já está executando um comando.");

        var watch = Stopwatch.StartNew();
        long totalRows = 0;
        var resultIndex = 0;
        var status = RunStatus.Completed;
        int? errorNumber = null;

        void OnInfo(object _, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError err in e.Errors) sink.Message(MessageKinds.Info, err.Message, null);
        }

        conn.InfoMessage += OnInfo;
        try
        {
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
                    cmd.StatementCompleted += (_, e) =>
                    {
                        if (e.RecordCount < 0) return;
                        sink.Message(MessageKinds.Rows, $"({e.RecordCount} {(e.RecordCount == 1 ? "linha afetada" : "linhas afetadas")})", null);
                        sink.StatementCompleted(e.RecordCount);
                    };

                    await using var reader = await cmd.ExecuteReaderAsync(cts.Token);
                    do
                    {
                        var (emitted, count, _) = await ResultStreamer.StreamCurrentAsync(reader, resultIndex, source, sink, maxRows, cts.Token);
                        if (emitted)
                        {
                            resultIndex++;
                            totalRows += count;
                        }
                    }
                    while (await reader.NextResultAsync(cts.Token));
                }
                catch (Exception ex) when (cts.IsCancellationRequested && ex is OperationCanceledException or SqlException or InvalidOperationException)
                {
                    status = RunStatus.Cancelled;
                    sink.Message(MessageKinds.Error, "Execução cancelada pelo usuário.", null);
                    break;
                }
                catch (SqlException ex)
                {
                    status = RunStatus.Error;
                    errorNumber = ex.Number;
                    foreach (SqlError err in ex.Errors)
                    {
                        // Dentro de procedure a linha é relativa a ela, não ao documento.
                        int? line = string.IsNullOrEmpty(err.Procedure) && err.LineNumber > 0 ? batchFirstLine + err.LineNumber - 1 : null;
                        sink.Message(err.Class > 10 || ex.Errors.Count == 1 ? MessageKinds.Error : MessageKinds.Info, err.Message, line);
                    }
                    break;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    status = RunStatus.Error;
                    sink.Message(MessageKinds.Error, $"A execução foi interrompida: {ex.Message}", null);
                    break;
                }
            }
        }
        finally
        {
            conn.InfoMessage -= OnInfo;
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
