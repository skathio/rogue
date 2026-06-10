using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Inventory;

public sealed record ReserveStockCommand(string Sku, int Quantity) : IRequest<bool>;

public sealed class ReserveStockCommandHandler : IRequestHandler<ReserveStockCommand, bool>
{
    public async Task<bool> Handle(ReserveStockCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return request.Quantity > 0;
    }
}
