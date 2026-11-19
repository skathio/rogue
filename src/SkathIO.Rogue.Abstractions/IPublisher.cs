using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Publishes events to all registered handlers.</summary>
public interface IPublisher
{
    /// <summary>Publishes an event to all handlers using the configured strategy.</summary>
    ValueTask Publish(IEvent @event, CancellationToken cancellationToken = default);

    /// <summary>Publishes via the object-dispatch path.</summary>
    ValueTask Publish(object @event, CancellationToken cancellationToken = default);
}
