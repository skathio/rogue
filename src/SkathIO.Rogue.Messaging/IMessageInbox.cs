using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Messaging;

/// <summary>
/// Remembers which messages have been handled, by <see cref="MessageEnvelope.Id"/>, so that a message
/// delivered more than once runs its handlers once. When one is registered, the
/// <see cref="MessageConsumer"/> claims each message's id before running its handlers: a message already
/// handled is acked without running them, one whose id another delivery holds is put back for later,
/// and one whose id is not valid (<see cref="MessageLimits.IsValidId"/>) is dead-lettered.
/// </summary>
/// <remarks>
/// The handlers' own writes and the inbox's record are not one transaction: if a process stops after
/// the handlers ran and before the record was written, the message runs again once the claim's lease
/// ends. A message that arrives without an id gets a fresh one from the transport on every delivery, so
/// its redeliveries cannot be told apart.
/// </remarks>
public interface IMessageInbox
{
    /// <summary>Claims <paramref name="messageId"/> for this delivery, unless it has been handled or another delivery holds it, and says which.</summary>
    ValueTask<InboxClaim> TryClaimAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>Records that the message's handlers succeeded, so later deliveries of it are skipped.</summary>
    ValueTask CompleteAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>Drops this delivery's claim after its handlers failed or were stopped, so the next delivery can claim the id at once.</summary>
    ValueTask ReleaseAsync(string messageId, CancellationToken cancellationToken);
}

/// <summary>The answer an <see cref="IMessageInbox"/> gives to a claim on a message id.</summary>
public enum InboxClaim
{
    /// <summary>This delivery holds the id and runs the handlers.</summary>
    Claimed = 0,

    /// <summary>The message has been handled; this delivery is a duplicate and is acked unhandled.</summary>
    Handled = 1,

    /// <summary>Another delivery holds the id and may be running the handlers now; this one is put back for later.</summary>
    Busy = 2,
}
