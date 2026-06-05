using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Strategy for dispatching a notification to its handlers.</summary>
public interface INotificationPublisher
{
    /// <summary>Publishes the notification to all handlers using this strategy.</summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken);
#else
    ValueTask Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken);
#endif
}

/// <summary>Wraps a notification handler invocation for use by <see cref="INotificationPublisher"/>.</summary>
public sealed class NotificationHandlerExecutor
{
    /// <summary>The handler instance (for diagnostics/logging).</summary>
    public object HandlerInstance { get; }

#if NETSTANDARD2_0
    private readonly Func<INotification, CancellationToken, ValueTask<Unit>> _handler;

    /// <summary>Initializes a new executor.</summary>
    public NotificationHandlerExecutor(object handlerInstance, Func<INotification, CancellationToken, ValueTask<Unit>> handler)
    {
        HandlerInstance = handlerInstance;
        _handler = handler;
    }
#else
    private readonly Func<INotification, CancellationToken, ValueTask> _handler;

    /// <summary>Initializes a new executor.</summary>
    public NotificationHandlerExecutor(object handlerInstance, Func<INotification, CancellationToken, ValueTask> handler)
    {
        HandlerInstance = handlerInstance;
        _handler = handler;
    }
#endif

    /// <summary>Invokes the handler.</summary>
#if NETSTANDARD2_0
    public ValueTask<Unit> ExecuteAsync(INotification notification, CancellationToken cancellationToken)
        => _handler(notification, cancellationToken);
#else
    public ValueTask ExecuteAsync(INotification notification, CancellationToken cancellationToken)
        => _handler(notification, cancellationToken);
#endif
}
