using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Smoke.Infrastructure;

namespace SkathIO.Rogue.Smoke.Application.Orders;

/// <summary>Second of two <see cref="OrderPlacedEvent"/> handlers — proves fan-out reaches every
/// registered handler, not just the first.</summary>
public sealed class UpdateInventoryHandler(IOrderActivityLog log) : IEventHandler<OrderPlacedEvent>
{
    public ValueTask Handle(OrderPlacedEvent @event, CancellationToken cancellationToken)
    {
        log.Record($"UpdateInventoryHandler: inventory updated for {@event.OrderId}");
        return default;
    }
}
