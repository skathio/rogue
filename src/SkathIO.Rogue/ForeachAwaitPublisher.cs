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
    public async ValueTask<Unit> Publish<TEvent>(IReadOnlyList<IEventHandler<TEvent>> handlers, TEvent ev, CancellationToken cancellationToken) where TEvent : IEvent
    {
        for (var i = 0; i < handlers.Count; i++)
        {
            await handlers[i].Handle(ev, cancellationToken).ConfigureAwait(false);
        }
        return Unit.Value;
    }
#else
    public async ValueTask Publish<TEvent>(IReadOnlyList<IEventHandler<TEvent>> handlers, TEvent ev, CancellationToken cancellationToken) where TEvent : IEvent
    {
        for (var i = 0; i < handlers.Count; i++)
        {
            await handlers[i].Handle(ev, cancellationToken).ConfigureAwait(false);
        }
    }
#endif
}
