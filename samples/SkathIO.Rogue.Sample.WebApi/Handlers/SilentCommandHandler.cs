using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>Handles <see cref="SilentCommand"/> with no response (FR-2, FR-8).</summary>
public sealed class SilentCommandHandler : IRequestHandler<SilentCommand>
{
    // net10 target: IRequestHandler<TRequest> returns bare ValueTask (the ns2.0 ValueTask<Unit>
    // shape does not apply here — this host is net10-only).
    /// <inheritdoc />
    public ValueTask Handle(SilentCommand request, CancellationToken cancellationToken)
        => default;
}
