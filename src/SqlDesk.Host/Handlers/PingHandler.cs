using SqlDesk.Host.Bridge;

namespace SqlDesk.Host.Handlers;

public sealed class PingHandler : MessageHandler<PingRequest, PingResponse>
{
    public override string Type => "ping";

    protected override Task<PingResponse> HandleAsync(PingRequest request, CancellationToken ct) =>
        Task.FromResult(new PingResponse($"pong: {request.Message}", DateTimeOffset.Now));
}
