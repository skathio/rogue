using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using SkathIO.Rogue.Jobs.Stores;

namespace SkathIO.Rogue.Jobs.Postgres;

// What an outbox and inbox need from the Postgres store: enqueueing in a transaction given only as a
// DbTransaction, and the inbox table of migration 3.
public sealed partial class PostgresJobStore : ITransactionalJobStore, IMessageInboxStore
{
    /// <inheritdoc />
    /// <remarks>The transaction must be an <see cref="NpgsqlTransaction"/>.</remarks>
    ValueTask ITransactionalJobStore.EnqueueAsync(IReadOnlyList<NewJob> jobs, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not NpgsqlTransaction npgsql)
        {
            throw new ArgumentException("The Postgres job store joins an NpgsqlTransaction only; this is a " + transaction.GetType() + ".", nameof(transaction));
        }
        return EnqueueAsync(jobs, npgsql, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>The token is the lease_until the claim wrote, in UTC ticks.</remarks>
    public async ValueTask<MessageClaimResult> TryClaimMessageAsync(string messageId, TimeSpan lease, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(messageId, nameof(messageId));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        DateTime now = Now();
        DateTime leaseEnd = now + lease;
        await using NpgsqlCommand command = _dataSource.CreateCommand(_sql.Inbox.Claim);
        command.Parameters.AddWithValue("id", messageId);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("lease_until", leaseEnd);
        object? outcome = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        MessageClaim claim = outcome is int answer ? (MessageClaim)answer : MessageClaim.Busy;
        return new MessageClaimResult(claim, claim == MessageClaim.Claimed ? leaseEnd.Ticks : 0);
    }

    /// <inheritdoc />
    public async ValueTask CompleteMessageAsync(string messageId, long claimToken, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await ClaimCommandAsync(_sql.Inbox.Complete, messageId, claimToken, cancellationToken).ConfigureAwait(false);
        command.Parameters.AddWithValue("now", Now());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ReleaseMessageAsync(string messageId, long claimToken, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await ClaimCommandAsync(_sql.Inbox.Release, messageId, claimToken, cancellationToken).ConfigureAwait(false);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Deletes handled message ids, and claims whose lease ended, before the given time; part of the prune.
    private Task<int> ForgetMessagesBeforeAsync(DateTime before, CancellationToken cancellationToken)
        => StoreSupport.DeleteInBatchesAsync(async ct =>
        {
            await using NpgsqlCommand command = _dataSource.CreateCommand(_sql.Inbox.Prune);
            command.Parameters.AddWithValue("before", before);
            command.Parameters.AddWithValue("batch", _options.PruneBatchSize);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, _options.PruneBatchSize, cancellationToken);

    // A statement on one message id's claim, named by its lease end as @lease_until.
    private async ValueTask<NpgsqlCommand> ClaimCommandAsync(string sql, string messageId, long claimToken, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(messageId, nameof(messageId));
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlCommand command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", messageId);
        command.Parameters.Add(new NpgsqlParameter("lease_until", NpgsqlDbType.TimestampTz) { Value = (object?)StoreSupport.InboxLeaseEnd(claimToken) ?? DBNull.Value });
        return command;
    }
}
