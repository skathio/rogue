using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Strategy for dispatching an event to its handlers (PD-42 — renamed from <c>INotificationPublisher</c>).</summary>
public interface IEventPublisher
{
    /// <summary>
    /// Publishes the event to all handlers using this strategy. Generic over <typeparamref name="TEvent"/>
    /// so the generated caller (which already knows the concrete event and handler types statically) can
    /// hand over its cached <see cref="IEventHandler{TEvent}"/> instances directly — no wrapper type, no
    /// per-handler closure, no boxed enumerator (publish-fanout-perf D2).
    /// </summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Publish<TEvent>(IReadOnlyList<IEventHandler<TEvent>> handlers, TEvent ev, CancellationToken cancellationToken) where TEvent : IEvent;
#else
    ValueTask Publish<TEvent>(IReadOnlyList<IEventHandler<TEvent>> handlers, TEvent ev, CancellationToken cancellationToken) where TEvent : IEvent;
#endif
}
