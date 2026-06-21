using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Notifications;

public sealed record LowStockAlert(string Sku, int Remaining) : INotification;

public sealed class LowStockAlertHandler : INotificationHandler<LowStockAlert>
{
    public async Task Handle(LowStockAlert notification, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }
}
