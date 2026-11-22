using System;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// The inbox a job store keeps for a message consumer: which inbound message ids have been handled, so
/// a message delivered twice runs its handlers once. A claim reserves an id for a lease while its
/// handlers run; completing it marks the id handled, and releasing it lets the next delivery claim it
/// at once. A claim whose lease ran out can be taken again. Handled ids are kept for the store's
/// retention of succeeded jobs, then pruned.
/// </summary>
/// <remarks>
/// A message id follows the rules of <see cref="JobLimits.CheckKey"/>: 1 to
/// <see cref="JobLimits.MaxKeyLength"/> characters, with no white space or control characters, compared
/// exactly (case-sensitively).
/// </remarks>
public interface IMessageInboxStore
{
    /// <summary>
    /// Claims <paramref name="messageId"/> for <paramref name="lease"/> from now, unless it has been
    /// handled or another claim's lease still holds it, and says which. A claim made carries the token
    /// that <see cref="CompleteMessageAsync"/> and <see cref="ReleaseMessageAsync"/> take back.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="messageId"/> is not a valid id, or <paramref name="lease"/> is not positive.</exception>
    ValueTask<MessageClaimResult> TryClaimMessageAsync(string messageId, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>
    /// Marks <paramref name="messageId"/> handled while the claim named by <paramref name="claimToken"/>
    /// still holds it, or while no claim does, so later deliveries of it are skipped. Once another claim
    /// has taken the id over after this one's lease ran out, it changes nothing.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="messageId"/> is not a valid id.</exception>
    ValueTask CompleteMessageAsync(string messageId, long claimToken, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the claim named by <paramref name="claimToken"/> on <paramref name="messageId"/>, so its next
    /// delivery can claim it at once. Once the message has been handled, or another claim has taken the
    /// id over after this one's lease ran out, it changes nothing.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="messageId"/> is not a valid id.</exception>
    ValueTask ReleaseMessageAsync(string messageId, long claimToken, CancellationToken cancellationToken);
}

/// <summary>The answer to a claim on an inbound message id.</summary>
public enum MessageClaim
{
    /// <summary>The caller holds the id for its lease and runs the handlers.</summary>
    Claimed = 0,

    /// <summary>The message has been handled; this delivery is a duplicate.</summary>
    Handled = 1,

    /// <summary>Another claim's lease holds the id: its handlers may be running now.</summary>
    Busy = 2,
}

/// <summary>
/// The answer to a claim on an inbound message id, with the token that names the claim to
/// <see cref="IMessageInboxStore.CompleteMessageAsync"/> and <see cref="IMessageInboxStore.ReleaseMessageAsync"/>.
/// </summary>
/// <param name="Outcome">Whether the caller holds the id, or why not.</param>
/// <param name="Token">
/// Names this claim when <paramref name="Outcome"/> is <see cref="MessageClaim.Claimed"/>, and is zero
/// otherwise. What it holds is the store's choice; callers only hand it back.
/// </param>
public readonly record struct MessageClaimResult(MessageClaim Outcome, long Token);
