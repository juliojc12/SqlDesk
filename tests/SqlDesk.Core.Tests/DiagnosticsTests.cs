using SqlDesk.Core.Diagnostics;
using SqlDesk.Core.Execution;

namespace SqlDesk.Core.Tests;

public class DiagnosticsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Registra_contexto_tipo_mensagem_e_stack()
    {
        var log = new ErrorLog(Path.Combine(_dir, "logs", "error.log"));
        Exception ex;
        try { throw new InvalidOperationException("falhou de verdade"); }
        catch (Exception e) { ex = e; }

        log.Write("Handler 'query.execute'", ex);

        var text = File.ReadAllText(log.FilePath);
        Assert.Contains("Handler 'query.execute'", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("falhou de verdade", text);
        Assert.Contains("Registra_contexto_tipo_mensagem_e_stack", text); // stack
    }

    [Fact]
    public void Acrescenta_sem_apagar_os_anteriores()
    {
        var log = new ErrorLog(Path.Combine(_dir, "error.log"));
        log.Write("um", new Exception("a"));
        log.Write("dois", new Exception("b"));

        var text = File.ReadAllText(log.FilePath);
        Assert.True(text.IndexOf("um", StringComparison.Ordinal) < text.IndexOf("dois", StringComparison.Ordinal));
    }

    [Fact]
    public void Gira_o_arquivo_ao_passar_do_limite()
    {
        var log = new ErrorLog(Path.Combine(_dir, "error.log"), maxBytes: 200);
        for (var i = 0; i < 10; i++) log.Write($"erro {i}", new Exception(new string('x', 100)));

        Assert.True(File.Exists(log.FilePath + ".1"));
        Assert.True(new FileInfo(log.FilePath).Length < 1500);
    }

    [Fact]
    public void Nunca_lanca_mesmo_sem_poder_gravar()
    {
        // O "diretório" do log é um arquivo: criar a pasta falha.
        var blocker = Path.Combine(_dir, "arquivo");
        File.WriteAllText(blocker, "x");
        var log = new ErrorLog(Path.Combine(blocker, "sub", "error.log"));

        var ex = Record.Exception(() => log.Write("ctx", new Exception("a")));

        Assert.Null(ex);
    }

    [Theory]
    [InlineData(null, false, 10_000)]
    [InlineData(5_000, false, 5_000)]
    [InlineData(50, false, 100)]            // abaixo do mínimo
    [InlineData(99_000_000, false, 1_000_000)] // acima do máximo
    [InlineData(-3, false, 100)]
    public void Limite_de_linhas_e_ajustado_a_faixa(int? pedido, bool semLimite, int esperado) =>
        Assert.Equal(esperado, RowLimits.Resolve(pedido, semLimite));

    [Fact]
    public void Sem_limite_so_quando_pedido_explicitamente()
    {
        Assert.Null(RowLimits.Resolve(5_000, noLimit: true));
        Assert.Null(RowLimits.Resolve(null, noLimit: true));
    }
}
