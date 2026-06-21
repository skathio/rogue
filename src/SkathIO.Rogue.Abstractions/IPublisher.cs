using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Publishes events to all registered handlers.</summary>
public interface IPublisher
{
    /// <summary>Publishes an event to all handlers using the configured strategy.</summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Publish(IEvent @event, CancellationToken cancellationToken = default);
#else
    ValueTask Publish(IEvent @event, CancellationToken cancellationToken = default);
#endif

    /// <summary>Publishes via the object-dispatch path.</summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Publish(object @event, CancellationToken cancellationToken = default);
#else
    ValueTask Publish(object @event, CancellationToken cancellationToken = default);
#endif
}
