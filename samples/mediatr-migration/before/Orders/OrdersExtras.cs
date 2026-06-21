using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Orders;

// Additional Orders handlers. Reuses the existing `Order` record from GetOrderQuery.cs and the
// `Unit` shape (RefundOrderCommand : IRequest<Unit>).

public sealed record GetOrderHistoryQuery(string CustomerId) : IRequest<IReadOnlyList<Order>>;

public sealed class GetOrderHistoryQueryHandler
    : IRequestHandler<GetOrderHistoryQuery, IReadOnlyList<Order>>
{
    public async Task<IReadOnlyList<Order>> Handle(
        GetOrderHistoryQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new[] { new Order($"ORD-{request.CustomerId}", "Closed") };
    }
}

public sealed record UpdateOrderStatusCommand(string OrderId, string Status) : IRequest<bool>;

public sealed class UpdateOrderStatusCommandHandler
    : IRequestHandler<UpdateOrderStatusCommand, bool>
{
    public async Task<bool> Handle(UpdateOrderStatusCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return request.Status.Length > 0;
    }
}

public sealed record CalculateOrderTotalQuery(string OrderId) : IRequest<decimal>;

public sealed class CalculateOrderTotalQueryHandler
    : IRequestHandler<CalculateOrderTotalQuery, decimal>
{
    public async Task<decimal> Handle(CalculateOrderTotalQuery request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return 19.99m;
    }
}

public sealed record RefundOrderCommand(string OrderId) : IRequest<Unit>;

public sealed class RefundOrderCommandHandler : IRequestHandler<RefundOrderCommand, Unit>
{
    public async Task<Unit> Handle(RefundOrderCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return Unit.Value;
    }
}
