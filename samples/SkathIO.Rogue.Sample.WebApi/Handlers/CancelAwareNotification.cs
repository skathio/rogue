using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Sample.WebApi;

/// <summary>
/// A notification whose handler observes the <see cref="CancellationToken"/>, so a cancelled token
/// surfaces an <see cref="System.OperationCanceledException"/> through the publish path (FR-15).
/// </summary>
public sealed record CancelAwareNotification(int Id) : INotification;

/// <summary>Handles <see cref="CancelAwareNotification"/>, honoring cancellation (FR-15).</summary>
public sealed class CancelAwareHandler : INotificationHandler<CancelAwareNotification>
{
    /// <inheritdoc />
    public ValueTask Handle(CancelAwareNotification notification, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return default;
    }
}
