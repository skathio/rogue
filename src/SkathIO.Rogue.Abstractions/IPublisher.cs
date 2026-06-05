using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Publishes notifications to all registered handlers.</summary>
public interface IPublisher
{
    /// <summary>Publishes a notification to all handlers using the configured strategy.</summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Publish(INotification notification, CancellationToken cancellationToken = default);
#else
    ValueTask Publish(INotification notification, CancellationToken cancellationToken = default);
#endif

    /// <summary>Publishes via the object-dispatch path.</summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Publish(object notification, CancellationToken cancellationToken = default);
#else
    ValueTask Publish(object notification, CancellationToken cancellationToken = default);
#endif
}
