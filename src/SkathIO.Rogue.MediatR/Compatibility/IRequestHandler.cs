using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Compatibility;

// MediatR-shaped request handlers. Self-contained (PD-46/PD-48): they declare their own Handle and
// constrain on the adapter's own IRequest — they do NOT extend the core ICommandHandler/IQueryHandler
// (and could not, since F8 forks an IRequest<T> to command-or-query per the [MapAsQuery] marker — see
// IRequest.cs). Shape mirrors MediatR's IRequestHandler<,> but uses ValueTask (Rogue's contract); the
// Phase 6.2 analyzer rewrites Task -> ValueTask in handler bodies. PD-48 (11.4): the generator's
// adapter-mapping rule discovers these handlers (WellKnownTypeNames.AdapterIRequestHandler2/1), maps
// each onto the CQS dispatch path by F8 ([MapAsQuery] -> IQuery, default -> ICommand), and emits a
// Send_X method + SendObject case + a registration under THIS adapter handler interface.
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    ValueTask<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

public interface IRequestHandler<in TRequest>
    where TRequest : IRequest<global::SkathIO.Rogue.Unit>
{
    ValueTask Handle(TRequest request, CancellationToken cancellationToken);
}
