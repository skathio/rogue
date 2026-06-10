using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Notifications;

public sealed record OrderPlaced(string OrderId) : INotification;

// Two handlers for the same notification — fan-out. Both return Task (ROGM002 fires for each) and
// migrate independently to ValueTask.
public sealed class OrderPlacedEmailHandler : INotificationHandler<OrderPlaced>
{
    public async Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }
}

public sealed class OrderPlacedAuditHandler : INotificationHandler<OrderPlaced>
{
    public async Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }
}
