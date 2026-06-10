using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace Notifications;

// Additional notification handlers exercising more fan-out shapes (PD-32 fork(a)). OrderShipped has
// two handlers; PaymentReceived has one; LowStockAlertAuditHandler is a *second* handler for the
// existing LowStockAlert notification (declared in LowStockAlert.cs) — extra fan-out coverage.

public sealed record OrderShipped(string OrderId) : INotification;

public sealed class OrderShippedEmailHandler : INotificationHandler<OrderShipped>
{
    public async Task Handle(OrderShipped notification, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }
}

public sealed class OrderShippedSmsHandler : INotificationHandler<OrderShipped>
{
    public async Task Handle(OrderShipped notification, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }
}

public sealed record PaymentReceived(string OrderId, decimal Amount) : INotification;

public sealed class PaymentReceivedHandler : INotificationHandler<PaymentReceived>
{
    public async Task Handle(PaymentReceived notification, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }
}

// Second handler for the existing LowStockAlert notification (fan-out).
public sealed class LowStockAlertAuditHandler : INotificationHandler<LowStockAlert>
{
    public async Task Handle(LowStockAlert notification, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }
}
