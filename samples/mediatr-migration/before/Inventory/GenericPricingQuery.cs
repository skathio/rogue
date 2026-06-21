using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Inventory;

public sealed record Price<T>(T Item, decimal Amount);

// Open-generic request: ROGM003 (Info, no auto-fix) fires for this type — the source generator
// cannot enumerate open generics, so the migrator restructures to a closed generic or uses the
// ReflectionMediator escape hatch. Included to verify ROGM003 fires and produces no code-fix.
public sealed record GenericPricingQuery<T>(T Item) : IRequest<Price<T>>;

public sealed class GenericPricingQueryHandler<T> : IRequestHandler<GenericPricingQuery<T>, Price<T>>
{
    public async Task<Price<T>> Handle(GenericPricingQuery<T> request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new Price<T>(request.Item, 0m);
    }
}
