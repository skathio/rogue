using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>
/// A request whose handler awaits a real delay so a cancelled <see cref="CancellationToken"/>
/// surfaces an <see cref="System.OperationCanceledException"/> at the dispatch boundary (FR-14).
/// </summary>
public sealed record DelayRequest(int DelayMs) : IRequest<DelayResponse>;

/// <summary>Response for <see cref="DelayRequest"/>.</summary>
public sealed record DelayResponse(bool Completed);

/// <summary>Handles <see cref="DelayRequest"/> by awaiting a cancellable delay (FR-14).</summary>
public sealed class DelayHandler : IRequestHandler<DelayRequest, DelayResponse>
{
    /// <inheritdoc />
    public async ValueTask<DelayResponse> Handle(DelayRequest request, CancellationToken cancellationToken)
    {
        await Task.Delay(request.DelayMs, cancellationToken).ConfigureAwait(false);
        return new DelayResponse(true);
    }
}
