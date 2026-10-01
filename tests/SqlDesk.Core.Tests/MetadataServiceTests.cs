using SqlDesk.Core.Metadata;

namespace SqlDesk.Core.Tests;

public class MetadataServiceTests
{
    private static readonly Guid Conn = Guid.NewGuid();

    private sealed record Phase(string Name, string? Message, bool LoadingAtThatMoment);

    private sealed class Harness
    {
        public MetadataService Service { get; }
        public List<Phase> Phases { get; } = [];
        public TaskCompletionSource Done { get; private set; } = new();

        public Harness(Func<Guid, Action<string, string?>, Task> loader) => Service = new MetadataService(loader);

        /// <summary>Registra, para cada aviso, se o serviço ainda se declarava "carregando": o frontend consulta o estado ao receber o aviso.</summary>
        public void OnPhase(string name, string? message)
        {
            lock (Phases) Phases.Add(new Phase(name, message, Service.IsLoading(Conn)));
            if (name is MetaPhase.Columns or MetaPhase.Error) Done.TrySetResult();
        }

        public bool Start(bool force = false)
        {
            Done = new TaskCompletionSource();
            return Service.StartLoad(Conn, force, OnPhase);
        }
    }

    [Fact]
    public async Task Aviso_final_de_colunas_chega_com_o_carregamento_ja_encerrado()
    {
        var h = new Harness((id, onPhase) =>
        {
            onPhase(MetaPhase.Objects, null);
            return Task.CompletedTask;
        });

        Assert.True(h.Start());
        await h.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([MetaPhase.Objects, MetaPhase.Columns], h.Phases.Select(p => p.Name));
        Assert.True(h.Phases[0].LoadingAtThatMoment);   // ainda carregando as colunas
        Assert.False(h.Phases[1].LoadingAtThatMoment);  // terminou: quem consultar agora não pode ver "carregando"
        Assert.False(h.Service.IsLoading(Conn));
    }

    [Fact]
    public async Task Falha_tambem_avisa_depois_de_encerrar_o_carregamento()
    {
        var h = new Harness((id, onPhase) => throw new InvalidOperationException("sem senha"));

        h.Start();
        await h.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var error = Assert.Single(h.Phases);
        Assert.Equal(MetaPhase.Error, error.Name);
        Assert.Equal("sem senha", error.Message);
        Assert.False(error.LoadingAtThatMoment);
    }

    [Fact]
    public async Task Nao_inicia_outro_carregamento_enquanto_um_esta_em_andamento()
    {
        var gate = new TaskCompletionSource();
        var h = new Harness(async (id, onPhase) => await gate.Task);

        Assert.True(h.Start());
        Assert.False(h.Service.StartLoad(Conn, true, h.OnPhase));
        Assert.True(h.Service.IsLoading(Conn));

        gate.SetResult();
        await h.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(h.Start(force: true)); // depois de terminar, pode recarregar
    }
}
