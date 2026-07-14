using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Smoke.Infrastructure;

namespace SkathIO.Rogue.Smoke.Application.Orders;

/// <summary>First of two <see cref="OrderPlacedEvent"/> handlers.</summary>
public sealed class SendOrderConfirmationHandler(IOrderActivityLog log) : IEventHandler<OrderPlacedEvent>
{
    public ValueTask Handle(OrderPlacedEvent @event, CancellationToken cancellationToken)
    {
        log.Record($"SendOrderConfirmationHandler: confirmed {@event.OrderId}");
        return default;
    }
}
