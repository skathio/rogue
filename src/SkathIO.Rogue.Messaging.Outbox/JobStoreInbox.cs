using System;
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

    public async ValueTask<InboxClaim> TryClaimAsync(string messageId, CancellationToken cancellationToken)
        => await _store.TryClaimMessageAsync(messageId, _lease, cancellationToken).ConfigureAwait(false) switch
        {
            MessageClaim.Claimed => InboxClaim.Claimed,
            MessageClaim.Handled => InboxClaim.Handled,
            _ => InboxClaim.Busy,
        };

    public ValueTask CompleteAsync(string messageId, CancellationToken cancellationToken)
        => _store.CompleteMessageAsync(messageId, cancellationToken);

    public ValueTask ReleaseAsync(string messageId, CancellationToken cancellationToken)
        => _store.ReleaseMessageAsync(messageId, cancellationToken);
}
