using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs;

// The inbox the in-memory store keeps for a message consumer. Every member runs under the store's one
// lock. Handled ids are kept up to the store's finished retention, oldest dropped first, as finished
// jobs are.
public sealed partial class InMemoryJobStore : IMessageInboxStore
{
    // Each claimed id's lease end, while its handlers run.
    private readonly Dictionary<string, DateTimeOffset> _claimedMessages = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    private readonly HashSet<string> _handledMessages = new HashSet<string>(StringComparer.Ordinal);
    private readonly Queue<string> _handledOrder = new Queue<string>();

    /// <inheritdoc />
    /// <remarks>The token is the claim's lease end, in UTC ticks.</remarks>
    public ValueTask<MessageClaimResult> TryClaimMessageAsync(string messageId, TimeSpan lease, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(messageId, nameof(messageId));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_handledMessages.Contains(messageId))
            {
                return new ValueTask<MessageClaimResult>(new MessageClaimResult(MessageClaim.Handled, 0));
            }
            DateTimeOffset now = _time.GetUtcNow();
            if (_claimedMessages.TryGetValue(messageId, out DateTimeOffset until) && until > now)
            {
                return new ValueTask<MessageClaimResult>(new MessageClaimResult(MessageClaim.Busy, 0));
            }
            DateTimeOffset leaseEnd = now + lease;
            _claimedMessages[messageId] = leaseEnd;
            return new ValueTask<MessageClaimResult>(new MessageClaimResult(MessageClaim.Claimed, leaseEnd.UtcTicks));
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteMessageAsync(string messageId, long claimToken, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(messageId, nameof(messageId));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_claimedMessages.TryGetValue(messageId, out DateTimeOffset until))
            {
                // Another claim took the id over: its delivery records the outcome.
                if (until.UtcTicks != claimToken)
                {
                    return default;
                }
                _claimedMessages.Remove(messageId);
            }
            if (_handledMessages.Add(messageId))
            {
                _handledOrder.Enqueue(messageId);
                while (_handledOrder.Count > _finishedRetention)
                {
                    _handledMessages.Remove(_handledOrder.Dequeue());
                }
            }
        }
        return default;
    }

    /// <inheritdoc />
    public ValueTask ReleaseMessageAsync(string messageId, long claimToken, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(messageId, nameof(messageId));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_claimedMessages.TryGetValue(messageId, out DateTimeOffset until) && until.UtcTicks == claimToken)
            {
                _claimedMessages.Remove(messageId);
            }
        }
        return default;
    }
}
