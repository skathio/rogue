using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Inventory;

public readonly record struct StockLevel(string Sku, int Available);

public sealed record CheckStockQuery(string Sku) : IRequest<StockLevel>;

public sealed class CheckStockQueryHandler : IRequestHandler<CheckStockQuery, StockLevel>
{
    public async Task<StockLevel> Handle(CheckStockQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new StockLevel(request.Sku, 42);
    }
}
