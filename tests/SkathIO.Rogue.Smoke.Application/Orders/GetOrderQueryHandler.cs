using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Smoke.Infrastructure;

namespace SkathIO.Rogue.Smoke.Application.Orders;

public sealed class GetOrderQueryHandler(IOrderStore store) : IQueryHandler<GetOrderQuery, OrderDto>
{
    public ValueTask<OrderDto> Handle(GetOrderQuery query, CancellationToken cancellationToken)
    {
        OrderRecord record = store.Get(query.OrderId) ?? throw new OrderNotFoundException(query.OrderId);
        return new ValueTask<OrderDto>(OrderDto.FromRecord(record));
    }
}
