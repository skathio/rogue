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
    public ValueTask<MessageClaim> TryClaimMessageAsync(string messageId, TimeSpan lease, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(messageId, nameof(messageId));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_handledMessages.Contains(messageId))
            {
                return new ValueTask<MessageClaim>(MessageClaim.Handled);
            }
            DateTimeOffset now = _time.GetUtcNow();
            if (_claimedMessages.TryGetValue(messageId, out DateTimeOffset until) && until > now)
            {
                return new ValueTask<MessageClaim>(MessageClaim.Busy);
            }
            _claimedMessages[messageId] = now + lease;
            return new ValueTask<MessageClaim>(MessageClaim.Claimed);
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(messageId, nameof(messageId));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _claimedMessages.Remove(messageId);
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
    public ValueTask ReleaseMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(messageId, nameof(messageId));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _claimedMessages.Remove(messageId);
        }
        return default;
    }
}
