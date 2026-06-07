using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>Handles <see cref="PingRequest"/> by echoing the message (FR-1, FR-7).</summary>
public sealed class PingHandler : IRequestHandler<PingRequest, PingResponse>
{
    /// <inheritdoc />
    public ValueTask<PingResponse> Handle(PingRequest request, CancellationToken cancellationToken)
        => new(new PingResponse(request.Message));
}
