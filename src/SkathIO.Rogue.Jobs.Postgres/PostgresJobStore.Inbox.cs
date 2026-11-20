using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
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
    public async ValueTask<MessageClaim> TryClaimMessageAsync(string messageId, TimeSpan lease, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(messageId, nameof(messageId));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        DateTime now = Now();
        await using NpgsqlCommand command = _dataSource.CreateCommand(_sql.Inbox.Claim);
        command.Parameters.AddWithValue("id", messageId);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("lease_until", now + lease);
        object? outcome = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return outcome is int answer ? (MessageClaim)answer : MessageClaim.Busy;
    }

    /// <inheritdoc />
    public ValueTask CompleteMessageAsync(string messageId, CancellationToken cancellationToken)
        => WriteMessageAsync(_sql.Inbox.Complete, messageId, true, cancellationToken);

    /// <inheritdoc />
    public ValueTask ReleaseMessageAsync(string messageId, CancellationToken cancellationToken)
        => WriteMessageAsync(_sql.Inbox.Release, messageId, false, cancellationToken);

    // Deletes handled message ids, and claims whose lease ended, before the given time; part of the prune.
    private Task<int> ForgetMessagesBeforeAsync(DateTime before, CancellationToken cancellationToken)
        => StoreSupport.DeleteInBatchesAsync(async ct =>
        {
            await using NpgsqlCommand command = _dataSource.CreateCommand(_sql.Inbox.Prune);
            command.Parameters.AddWithValue("before", before);
            command.Parameters.AddWithValue("batch", _options.PruneBatchSize);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, _options.PruneBatchSize, cancellationToken);

    // Runs a write on one message id, with the store's time as @now when the statement takes it.
    private async ValueTask WriteMessageAsync(string sql, string messageId, bool withNow, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(messageId, nameof(messageId));
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", messageId);
        if (withNow)
        {
            command.Parameters.AddWithValue("now", Now());
        }
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
