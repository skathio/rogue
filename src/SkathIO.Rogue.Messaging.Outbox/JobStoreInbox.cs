using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Jobs;

namespace SkathIO.Rogue.Messaging.Outbox;

/// <summary>The consumer's <see cref="IMessageInbox"/>, kept by the job store.</summary>
internal sealed class JobStoreInbox : IMessageInbox
{
    private readonly IMessageInboxStore _store;
    private readonly TimeSpan _lease;

    public JobStoreInbox(IJobStore store, TimeSpan lease)
    {
        _store = store as IMessageInboxStore
            ?? throw new InvalidOperationException("The job store " + store.GetType() + " keeps no message inbox. Register a jobs store that does, such as AddRoguePostgresJobs or AddRogueSqlServerJobs.");
        _lease = lease;
    }

    public async ValueTask<InboxClaimResult> TryClaimAsync(string messageId, CancellationToken cancellationToken)
    {
        MessageClaimResult claim = await _store.TryClaimMessageAsync(messageId, _lease, cancellationToken).ConfigureAwait(false);
        InboxClaim outcome = claim.Outcome switch
        {
            MessageClaim.Claimed => InboxClaim.Claimed,
            MessageClaim.Handled => InboxClaim.Handled,
            _ => InboxClaim.Busy,
        };
        return new InboxClaimResult(outcome, claim.Token);
    }

    public ValueTask CompleteAsync(string messageId, long claimToken, CancellationToken cancellationToken)
        => _store.CompleteMessageAsync(messageId, claimToken, cancellationToken);

    public ValueTask ReleaseAsync(string messageId, long claimToken, CancellationToken cancellationToken)
        => _store.ReleaseMessageAsync(messageId, claimToken, cancellationToken);

    public ValueTask<InboxClaimResult> TryClaimAsync(string messageName, string messageId, CancellationToken cancellationToken)
        => TryClaimAsync(Key(messageName, messageId), cancellationToken);

    public ValueTask CompleteAsync(string messageName, string messageId, long claimToken, CancellationToken cancellationToken)
        => CompleteAsync(Key(messageName, messageId), claimToken, cancellationToken);

    public ValueTask ReleaseAsync(string messageName, string messageId, long claimToken, CancellationToken cancellationToken)
        => ReleaseAsync(Key(messageName, messageId), claimToken, cancellationToken);

    /// <summary>
    /// The store key of a message: its name, '/', and its id, so ids of different names never meet. A
    /// name cannot hold '/', so the first one ends it. A key longer than the store keeps is its SHA-256
    /// instead, which holds no '/' and so never equals a key kept as it is.
    /// </summary>
    private static string Key(string messageName, string messageId)
    {
        ArgumentNullException.ThrowIfNull(messageName);
        ArgumentNullException.ThrowIfNull(messageId);
        string key = messageName + "/" + messageId;
        return key.Length <= MessageLimits.MaxIdLength
            ? key
            : "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
}
