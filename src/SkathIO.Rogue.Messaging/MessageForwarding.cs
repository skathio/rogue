using System;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace SkathIO.Rogue.Messaging;

/// <summary>
/// Sends events marked <see cref="MessageAttribute"/> to the bus when they are published through
/// <see cref="IPublisher"/>. The dispatcher the source generator emits calls it; application code does not.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class MessageForwarding
{
    /// <summary>
    /// Completes when <paramref name="localHandlers"/> has, and then, when <paramref name="event"/> is a
    /// marked event and <c>AddRogueMessaging</c> was called, once it has been sent to the bus. An event
    /// whose local handlers fail is not sent. An event being handled because it arrived from the bus is
    /// not sent back.
    /// </summary>
    public static ValueTask AfterLocalHandlers(IServiceProvider services, IEvent @event, ValueTask localHandlers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(@event);
        if (services.GetService<MessageBus>() is not { } bus)
        {
            return localHandlers;
        }
        BoundMessage? bound;
        try
        {
            bound = bus.ForwardingFor(@event);
        }
        catch (Exception ex)
        {
            // The registry could not be built. The local handlers already started, so their outcome is
            // still awaited before the configuration error is reported.
            return FailAfterAsync(localHandlers, ExceptionDispatchInfo.Capture(ex));
        }
        return bound is null ? localHandlers : bus.ForwardAfterAsync(bound, @event, localHandlers, cancellationToken);
    }

    private static async ValueTask FailAfterAsync(ValueTask localHandlers, ExceptionDispatchInfo error)
    {
        await localHandlers.ConfigureAwait(false);
        error.Throw();
    }
}
