using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>
/// Publishes events sequentially. On the first handler exception, enumeration stops
/// and the exception propagates (first-exception-wins semantics, FR-29 E1).
/// </summary>
public sealed class ForeachAwaitPublisher : IEventPublisher
{
    /// <inheritdoc/>
#if NETSTANDARD2_0
    public async ValueTask<Unit> Publish(IEnumerable<EventHandlerExecutor> handlerExecutors, IEvent @event, CancellationToken cancellationToken)
    {
        foreach (var executor in handlerExecutors)
        {
            await executor.ExecuteAsync(@event, cancellationToken).ConfigureAwait(false);
        }
        return Unit.Value;
    }
#else
    public async ValueTask Publish(IEnumerable<EventHandlerExecutor> handlerExecutors, IEvent @event, CancellationToken cancellationToken)
    {
        foreach (var executor in handlerExecutors)
        {
            await executor.ExecuteAsync(@event, cancellationToken).ConfigureAwait(false);
        }
    }
#endif
}
