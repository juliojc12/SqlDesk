using SqlDesk.Core.Metadata;
using SqlDesk.Host.Bridge;

namespace SqlDesk.Host.Handlers;

/// <summary>
/// Dispara o carregamento do cache de metadados da conexão em segundo plano (schemas e objetos primeiro, colunas em
/// seguida) e avisa o frontend por <c>metadata.updated</c>. Sem <c>force</c>, não recarrega um cache já completo.
/// </summary>
public sealed class MetadataRefreshHandler(MetadataService metadata, EventHub hub) : MessageHandler<MetadataRefreshRequest, MetadataRefreshResponse>
{
    public override string Type => "metadata.refresh";

    protected override Task<MetadataRefreshResponse> HandleAsync(MetadataRefreshRequest r, CancellationToken ct)
    {
        var id = r.ConnectionId;
        if (r.Force) metadata.Invalidate(id);
        var started = metadata.StartLoad(id, r.Force,
            (phase, message) => hub.Publish("metadata.updated", new MetadataUpdatedEvent(id, phase, message)));
        return Task.FromResult(new MetadataRefreshResponse(started, metadata.IsLoading(id), metadata.Get(id) is not null));
    }
}

public sealed class MetadataGetHandler(MetadataService metadata) : MessageHandler<MetadataGetRequest, MetadataDto>
{
    public override string Type => "metadata.get";

    protected override Task<MetadataDto> HandleAsync(MetadataGetRequest r, CancellationToken ct)
    {
        var s = metadata.Get(r.ConnectionId);
        var dto = s is null
            ? new MetadataDto(false, false, metadata.IsLoading(r.ConnectionId), [], [], new Dictionary<string, IReadOnlyList<MetaColumn>>())
            : new MetadataDto(true, s.ColumnsLoaded, metadata.IsLoading(r.ConnectionId), s.Schemas, s.Objects, s.Columns);
        return Task.FromResult(dto);
    }
}
