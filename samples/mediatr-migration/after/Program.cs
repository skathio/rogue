// After migration: SkathIO.Rogue-based code. Illustrative excerpt from the full domain-grouped
// `before/` sample (Catalog, Orders, Customers, Inventory, Shipping, Notifications, Reporting) after
// the migration code-fixes run:
//   ROGM001 rewrites `using MediatR;` -> `using SkathIO.Rogue;`
//   ROGM002 rewrites handler `Task<T>` / `Task` return types -> `ValueTask<T>` / `ValueTask`
// (the compat `using SkathIO.Rogue.Compatibility;` is added by hand only for DI-only helpers such as
// AddMediatR — see docs/migration-guide.md). The end-to-end migration is exercised by the AC-F gate
// in tests/SkathIO.Rogue.Migration.Tests/MigrationGateTests.cs.
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;

// Query (request/response):
public sealed record Product(string Name, decimal Price);
public sealed record GetProductQuery(string Sku) : IRequest<Product>;
public sealed class GetProductQueryHandler : IRequestHandler<GetProductQuery, Product>
{
    public async ValueTask<Product> Handle(GetProductQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new Product($"Product {request.Sku}", 9.99m);
    }
}

// Void command (returns Unit):
public sealed record CancelOrderCommand(string OrderId) : IRequest;
public sealed class CancelOrderCommandHandler : IRequestHandler<CancelOrderCommand>
{
    public async ValueTask Handle(CancelOrderCommand request, CancellationToken cancellationToken)
        => await Task.CompletedTask;
}

// Notification with fan-out (multiple handlers):
public sealed record OrderPlaced(string OrderId) : INotification;
public sealed class OrderPlacedEmailHandler : INotificationHandler<OrderPlaced>
{
    public async ValueTask Handle(OrderPlaced notification, CancellationToken cancellationToken)
        => await Task.CompletedTask;
}
