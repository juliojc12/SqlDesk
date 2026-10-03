using System.Diagnostics;
using SqlDesk.Core.Execution;

namespace SqlDesk.Integration.Tests;

/// <summary>Fluxos principais de execução (grade, cancelamento, erro, scripts) contra MySQL e MariaDB de verdade.</summary>
public class ExecutionTests
{
    private static string Table(string server) => $"ex_{server}_{Guid.NewGuid().ToString("N")[..8]}";

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Executa_select_e_devolve_linhas(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var (summary, _, cap) = await h.RunCapturingAsync("SELECT 1 AS a, 'x' AS b UNION ALL SELECT 2, 'y'", raw: false);
        Assert.Equal(RunStatus.Completed, summary.Status);
        Assert.Equal(2, cap.Sets.Single().Sample.Count);
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Cancela_consulta_longa_e_a_aba_continua_usavel(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var watch = Stopwatch.StartNew();
        var run = h.RunCapturingAsync("SELECT SLEEP(30)", raw: false);
        await Task.Delay(500);
        h.Runner.Cancel(h.TabId);
        var (summary, rows, _) = await run;
        watch.Stop();

        Assert.Equal(RunStatus.Cancelled, summary.Status);
        // O servidor interrompe com erro (cancelado de verdade) ou atende o KILL QUERY sem erro (SLEEP devolve 1): nesse
        // caso não dá para saber se o comando foi interrompido ou terminou, e a mensagem diz isso em vez de afirmar que nada
        // aconteceu.
        var cancelMessages = rows.Messages.Where(m => m.Kind == MessageKinds.Error &&
            (m.Text == "Execução cancelada pelo usuário." ||
             m.Text == "Cancelamento pedido, mas o servidor devolveu o resultado sem erro: o comando pode ter sido interrompido ou já ter terminado. Confira os dados (as alterações podem ter sido gravadas).")).ToList();
        Assert.Single(cancelMessages);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"o cancelamento demorou {watch.Elapsed}");

        var (after, _, cap) = await h.RunCapturingAsync("SELECT 1", raw: false);
        Assert.Equal(RunStatus.Completed, after.Status);
        Assert.Single(cap.Sets.Single().Sample);
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Erro_de_sintaxe_vira_mensagem_e_nao_excecao(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var (summary, rows, _) = await h.RunCapturingAsync("SELEC 1", raw: false);
        Assert.Equal(RunStatus.Error, summary.Status);
        Assert.Contains(rows.Messages, m => m.Kind == MessageKinds.Error);
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Script_com_varios_statements_e_ponto_e_virgula_em_string(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var (summary, _, cap) = await h.RunCapturingAsync("SELECT ';' AS p; SELECT 2 AS q", raw: false);
        Assert.Equal(RunStatus.Completed, summary.Status);
        Assert.Equal(2, cap.Sets.Count);
        Assert.Equal(";", cap.Sets[0].Sample.Single()[0]);
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Script_com_varios_resultados(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var (summary, _, cap) = await h.RunCapturingAsync("SELECT 1; SELECT 2, 3; SELECT 4", raw: false);
        Assert.Equal(RunStatus.Completed, summary.Status);
        Assert.Equal(3, cap.Sets.Count);
        Assert.Equal(2, cap.Sets[1].Columns.Count);
    }

    [IntegrationTheory, MemberData(nameof(TestServers.All), MemberType = typeof(TestServers))]
    public async Task Insert_e_update_informam_linhas_afetadas(string server)
    {
        await using var h = await Harness.OpenAsync(server);
        var t = Table(server);
        try
        {
            await h.RunAsync($"CREATE TABLE {t} (id INT PRIMARY KEY, v INT) ENGINE=InnoDB");

            var (s1, r1, _) = await h.RunCapturingAsync($"INSERT INTO {t} VALUES (1, 0), (2, 0), (3, 0)", raw: false);
            Assert.Equal(RunStatus.Completed, s1.Status);
            Assert.Contains(r1.Messages, m => m.Kind == MessageKinds.Rows && m.Text.Contains("3 linhas afetadas"));

            var (s2, r2, _) = await h.RunCapturingAsync($"UPDATE {t} SET v = 9 WHERE id = 1", raw: false);
            Assert.Equal(RunStatus.Completed, s2.Status);
            Assert.Contains(r2.Messages, m => m.Kind == MessageKinds.Rows && m.Text.Contains("1 linha afetada"));
        }
        finally
        {
            await h.RunAsync($"DROP TABLE IF EXISTS {t}");
        }
    }
}
