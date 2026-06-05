using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>
/// Publishes notifications sequentially. On the first handler exception, enumeration stops
/// and the exception propagates (first-exception-wins semantics, FR-29 E1).
/// </summary>
public sealed class ForeachAwaitPublisher : INotificationPublisher
{
    /// <inheritdoc/>
#if NETSTANDARD2_0
    public async ValueTask<Unit> Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        foreach (var executor in handlerExecutors)
        {
            await executor.ExecuteAsync(notification, cancellationToken).ConfigureAwait(false);
        }
        return Unit.Value;
    }
#else
    public async ValueTask Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        foreach (var executor in handlerExecutors)
        {
            await executor.ExecuteAsync(notification, cancellationToken).ConfigureAwait(false);
        }
    }
#endif
}
