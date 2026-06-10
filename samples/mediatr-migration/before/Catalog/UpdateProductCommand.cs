using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Catalog;

// Explicit IRequest<Unit> / IRequestHandler<,Unit> shape (vs the parameterless IRequest above) —
// MediatR users write both; the migration must handle the Unit-returning variant too.
public sealed record UpdateProductCommand(string Sku, decimal Price) : IRequest<Unit>;

public sealed class UpdateProductCommandHandler : IRequestHandler<UpdateProductCommand, Unit>
{
    public async Task<Unit> Handle(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return Unit.Value;
    }
}
