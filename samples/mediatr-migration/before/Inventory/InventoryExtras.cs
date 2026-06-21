using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Inventory;

// Additional Inventory handlers. Reuses the existing `StockLevel` struct from CheckStockQuery.cs and
// introduces a minimal `InventoryReport` DTO (constructible with `new`).

public sealed record InventoryReport(int TotalSkus, int LowStockCount);

public sealed record RestockInventoryCommand(string Sku, int Quantity) : IRequest<bool>;

public sealed class RestockInventoryCommandHandler : IRequestHandler<RestockInventoryCommand, bool>
{
    public async Task<bool> Handle(RestockInventoryCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return request.Quantity > 0;
    }
}

public sealed record GetLowStockItemsQuery(int Threshold) : IRequest<IReadOnlyList<StockLevel>>;

public sealed class GetLowStockItemsQueryHandler
    : IRequestHandler<GetLowStockItemsQuery, IReadOnlyList<StockLevel>>
{
    public async Task<IReadOnlyList<StockLevel>> Handle(
        GetLowStockItemsQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new[] { new StockLevel("SKU-LOW", request.Threshold) };
    }
}

public sealed record AdjustInventoryCommand(string Sku, int Delta) : IRequest;

public sealed class AdjustInventoryCommandHandler : IRequestHandler<AdjustInventoryCommand>
{
    public async Task Handle(AdjustInventoryCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }
}

public sealed record GetInventoryReportQuery(string Warehouse) : IRequest<InventoryReport>;

public sealed class GetInventoryReportQueryHandler
    : IRequestHandler<GetInventoryReportQuery, InventoryReport>
{
    public async Task<InventoryReport> Handle(
        GetInventoryReportQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new InventoryReport(TotalSkus: 100, LowStockCount: 3);
    }
}
