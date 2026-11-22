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
    /// <summary>
    /// Claims <paramref name="messageId"/> for this delivery, unless it has been handled or another
    /// delivery holds it, and says which. A claim made carries the token that names it to
    /// <see cref="CompleteAsync(string, long, CancellationToken)"/> and <see cref="ReleaseAsync(string, long, CancellationToken)"/>.
    /// </summary>
    ValueTask<InboxClaimResult> TryClaimAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Records that the message's handlers succeeded, so later deliveries of it are skipped. It acts
    /// only on the claim <paramref name="claimToken"/> names: once another delivery has taken the id
    /// over after this claim's lease ran out, it changes nothing.
    /// </summary>
    ValueTask CompleteAsync(string messageId, long claimToken, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the claim <paramref name="claimToken"/> names after its handlers failed or were stopped, so
    /// the next delivery can claim the id at once. Once another delivery has taken the id over after this
    /// claim's lease ran out, it changes nothing.
    /// </summary>
    ValueTask ReleaseAsync(string messageId, long claimToken, CancellationToken cancellationToken);

    /// <summary>
    /// Claims the id of a message named <paramref name="messageName"/>, the call the
    /// <see cref="MessageConsumer"/> makes. An inbox that overrides it keeps ids apart per name, so a
    /// message of one name is never skipped because one of another name had the same id; this default
    /// ignores the name.
    /// </summary>
    ValueTask<InboxClaimResult> TryClaimAsync(string messageName, string messageId, CancellationToken cancellationToken)
        => TryClaimAsync(messageId, cancellationToken);

    /// <summary>Records that the handlers of the message named <paramref name="messageName"/> succeeded; see <see cref="TryClaimAsync(string, string, CancellationToken)"/>.</summary>
    ValueTask CompleteAsync(string messageName, string messageId, long claimToken, CancellationToken cancellationToken)
        => CompleteAsync(messageId, claimToken, cancellationToken);

    /// <summary>Drops the claim on the message named <paramref name="messageName"/>; see <see cref="TryClaimAsync(string, string, CancellationToken)"/>.</summary>
    ValueTask ReleaseAsync(string messageName, string messageId, long claimToken, CancellationToken cancellationToken)
        => ReleaseAsync(messageId, claimToken, cancellationToken);
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

/// <summary>
/// An <see cref="IMessageInbox"/>'s answer to a claim, with the token that names the claim to its
/// complete and release calls.
/// </summary>
/// <param name="Outcome">Whether this delivery holds the id, or why not.</param>
/// <param name="Token">
/// Names this claim when <paramref name="Outcome"/> is <see cref="InboxClaim.Claimed"/>, and is zero
/// otherwise. What it holds is the inbox's choice; callers only hand it back.
/// </param>
public readonly record struct InboxClaimResult(InboxClaim Outcome, long Token);
