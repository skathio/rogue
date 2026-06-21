using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Orders;

public sealed record OrderResult(string OrderId, bool Accepted);

public sealed record PlaceOrderCommand(string CustomerId, string Sku, int Quantity) : IRequest<OrderResult>;

public sealed class PlaceOrderCommandHandler : IRequestHandler<PlaceOrderCommand, OrderResult>
{
    public async Task<OrderResult> Handle(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new OrderResult($"ORD-{request.Sku}", Accepted: request.Quantity > 0);
    }
}
