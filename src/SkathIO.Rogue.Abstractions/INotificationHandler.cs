using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>Handles a notification of type <typeparamref name="TNotification"/>.</summary>
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    /// <summary>Handles the notification.</summary>
#if NETSTANDARD2_0
    ValueTask<Unit> Handle(TNotification notification, CancellationToken cancellationToken);
#else
    ValueTask Handle(TNotification notification, CancellationToken cancellationToken);
#endif
}
