using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Strategy for dispatching an event to its handlers (PD-42 — renamed from <c>INotificationPublisher</c>).</summary>
public interface IEventPublisher
{
    /// <summary>Publishes the event to all handlers using this strategy.</summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Publish(IEnumerable<EventHandlerExecutor> handlerExecutors, IEvent @event, CancellationToken cancellationToken);
#else
    ValueTask Publish(IEnumerable<EventHandlerExecutor> handlerExecutors, IEvent @event, CancellationToken cancellationToken);
#endif
}

/// <summary>Wraps an event handler invocation for use by <see cref="IEventPublisher"/>.</summary>
public sealed class EventHandlerExecutor
{
    /// <summary>The handler instance (for diagnostics/logging).</summary>
    public object HandlerInstance { get; }

#if NETSTANDARD2_0
    private readonly Func<IEvent, CancellationToken, ValueTask<Unit>> _handler;

    /// <summary>Initializes a new executor.</summary>
    public EventHandlerExecutor(object handlerInstance, Func<IEvent, CancellationToken, ValueTask<Unit>> handler)
    {
        HandlerInstance = handlerInstance;
        _handler = handler;
    }
#else
    private readonly Func<IEvent, CancellationToken, ValueTask> _handler;

    /// <summary>Initializes a new executor.</summary>
    public EventHandlerExecutor(object handlerInstance, Func<IEvent, CancellationToken, ValueTask> handler)
    {
        HandlerInstance = handlerInstance;
        _handler = handler;
    }
#endif

    /// <summary>Invokes the handler.</summary>
#if NETSTANDARD2_0
    public ValueTask<Unit> ExecuteAsync(IEvent @event, CancellationToken cancellationToken)
        => _handler(@event, cancellationToken);
#else
    public ValueTask ExecuteAsync(IEvent @event, CancellationToken cancellationToken)
        => _handler(@event, cancellationToken);
#endif
}
