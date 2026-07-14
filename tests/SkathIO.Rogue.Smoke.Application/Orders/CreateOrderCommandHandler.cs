using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Smoke.Infrastructure;

namespace SkathIO.Rogue.Smoke.Application.Orders;

public sealed class CreateOrderCommandHandler(IOrderStore store, IPublisher publisher, IOrderActivityLog log)
    : ICommandHandler<CreateOrderCommand, OrderId>
{
    public async ValueTask<OrderId> Handle(CreateOrderCommand request, CancellationToken cancellationToken)
    {
        OrderRecord record = store.Add(request.ProductId, request.Quantity);
        log.Record($"CreateOrderCommandHandler: created {record.Id}");

        await publisher.Publish(new OrderPlacedEvent(record.Id), cancellationToken).ConfigureAwait(false);

        return new OrderId(record.Id);
    }
}
