using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>
/// Publishes events sequentially. On the first handler exception, enumeration stops
/// and the exception propagates (first exception wins; later handlers do not run).
/// </summary>
public sealed class ForeachAwaitPublisher : IEventPublisher
{
    /// <inheritdoc/>
    public async ValueTask Publish<TEvent>(IReadOnlyList<IEventHandler<TEvent>> handlers, TEvent ev, CancellationToken cancellationToken) where TEvent : IEvent
    {
        for (var i = 0; i < handlers.Count; i++)
        {
            await handlers[i].Handle(ev, cancellationToken).ConfigureAwait(false);
        }
    }
}
