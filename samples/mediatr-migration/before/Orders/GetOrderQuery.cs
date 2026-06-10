using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Orders;

public sealed record Order(string OrderId, string Status);

public sealed record GetOrderQuery(string OrderId) : IRequest<Order>;

// Already migrated by hand: this handler returns ValueTask<Order>, not Task<Order>. ROGM002 keys off
// a Task return type, so it correctly does NOT fire here — verifying the migration leaves
// already-ValueTask code untouched. (It is a plain class rather than an IRequestHandler<,>
// implementation precisely because the MediatR stub interface is Task-based; on the Rogue side the
// real IRequestHandler<,> is ValueTask-based and this shape lines up with it.)
public sealed class GetOrderQueryHandler
{
    public ValueTask<Order> Handle(GetOrderQuery request, CancellationToken cancellationToken)
        => new(new Order(request.OrderId, "Open"));
}
