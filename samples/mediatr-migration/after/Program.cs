// After migration: SkathIO.Rogue-based code, migrated directly to the post-D5 CQS contracts
// (ICommand/IQuery/IEvent + their handlers — NOT a core IRequest, which the clean break removed).
// Illustrative excerpt from the full domain-grouped `before/` sample (Catalog, Orders, Customers,
// Inventory, Shipping, Notifications, Reporting) after the migration code-fixes run:
//   ROGM001 rewrites `using MediatR;` -> `using SkathIO.Rogue;`
//   ROGM002 rewrites handler `Task<T>` / `Task` return types -> `ValueTask<T>` / `ValueTask`
//   ROGM006 rewrites the MediatR marker/handler base lists onto the CQS contracts:
//     IRequest<T>            -> IQuery<T>   (name signals a read)  /  ICommand<T> (default; ambiguous → ROGM005)
//     IRequest              -> ICommand    (void command)
//     IRequestHandler<TReq,TResp> -> IQueryHandler<,> / ICommandHandler<,> (to match the request)
//     IRequestHandler<TReq> -> ICommandHandler<TReq>
//     INotification         -> IEvent ;  INotificationHandler<T> -> IEventHandler<T>
// The migration goes directly to IQuery<T> for reads, so the adapter-only [MapAsQuery] attribute is
// not needed on this path — query intent is expressed by the contract itself. The end-to-end
// migration is exercised by the AC-F gate in
// tests/SkathIO.Rogue.Migration.Tests/MigrationGateTests.cs.
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;

// Query (read; name signals a read → IQuery<T> / IQueryHandler<,>):
public sealed record Product(string Name, decimal Price);
public sealed record GetProductQuery(string Sku) : IQuery<Product>;
public sealed class GetProductQueryHandler : IQueryHandler<GetProductQuery, Product>
{
    public async ValueTask<Product> Handle(GetProductQuery query, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new Product($"Product {query.Sku}", 9.99m);
    }
}

// Command (write; name signals a write → ICommand<T> / ICommandHandler<,>):
public sealed record CreateProductCommand(string Name, decimal Price) : ICommand<bool>;
public sealed class CreateProductCommandHandler : ICommandHandler<CreateProductCommand, bool>
{
    public async ValueTask<bool> Handle(CreateProductCommand command, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return command.Name.Length > 0;
    }
}

// Void command (no response → ICommand / ICommandHandler<TCommand>):
public sealed record CancelOrderCommand(string OrderId) : ICommand;
public sealed class CancelOrderCommandHandler : ICommandHandler<CancelOrderCommand>
{
    public async ValueTask Handle(CancelOrderCommand command, CancellationToken cancellationToken)
        => await Task.CompletedTask;
}

// Event with fan-out (multiple handlers; INotification -> IEvent, INotificationHandler<T> -> IEventHandler<T>):
public sealed record OrderPlaced(string OrderId) : IEvent;
public sealed class OrderPlacedEmailHandler : IEventHandler<OrderPlaced>
{
    public async ValueTask Handle(OrderPlaced @event, CancellationToken cancellationToken)
        => await Task.CompletedTask;
}
