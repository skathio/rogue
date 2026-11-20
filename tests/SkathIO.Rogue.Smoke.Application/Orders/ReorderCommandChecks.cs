using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Smoke.Infrastructure;
using SkathIO.Rogue.Validation;

namespace SkathIO.Rogue.Smoke.Application.Orders;

/// <summary>
/// The async rule of <see cref="ReorderCommand"/>: the source order must exist. Registered by the
/// generator (no wiring call) and resolved from DI with the Infrastructure layer's store.
/// </summary>
public sealed class ReorderCommandChecks(IOrderStore store) : IAsyncRules<ReorderCommand>
{
    public ValueTask CheckAsync(ReorderCommand request, ValidationErrors errors, CancellationToken cancellationToken)
    {
        if (store.Get(request.SourceOrderId) is null)
        {
            errors.Add(nameof(ReorderCommand.SourceOrderId), "order.not_found", "The source order does not exist.");
        }
        return default;
    }
}
