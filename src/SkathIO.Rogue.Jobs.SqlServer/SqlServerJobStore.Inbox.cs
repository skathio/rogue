using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SkathIO.Rogue.Jobs.Stores;

namespace SkathIO.Rogue.Jobs.SqlServer;

// What an outbox and inbox need from the SQL Server store: enqueueing in the caller's transaction, and
// the inbox table of migration 3.
public sealed partial class SqlServerJobStore : ITransactionalJobStore, IMessageInboxStore
{
    /// <inheritdoc />
    /// <remarks>The transaction must be a <see cref="SqlTransaction"/>; the whole batch is one INSERT over a table-valued parameter.</remarks>
    async ValueTask ITransactionalJobStore.EnqueueAsync(IReadOnlyList<NewJob> jobs, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        StoreSupport.CheckBatch(jobs);
        if (transaction is not SqlTransaction sql)
        {
            throw new ArgumentException("The SQL Server job store joins a SqlTransaction only; this is a " + transaction.GetType() + ".", nameof(transaction));
        }

        // SqlClient clears a transaction's connection once it commits or rolls back. Enqueueing on that
        // connection outside the transaction would store the jobs apart from the caller's data.
        SqlConnection connection = sql.Connection ?? throw new InvalidOperationException("The transaction has already been committed or rolled back.");
        if (jobs.Count == 0)
        {
            return;
        }
        await _migrations.ReadyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(_sql.Insert, connection, sql);
        command.Parameters.Add(SqlServerRows.Jobs("@jobs", _sql.JobRows, jobs));
        command.Parameters.Add(Time("@now", Now()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>The token is the lease_until the claim wrote, in UTC ticks.</remarks>
    public async ValueTask<MessageClaimResult> TryClaimMessageAsync(string messageId, TimeSpan lease, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(messageId, nameof(messageId));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        DateTime now = Now();
        DateTime leaseEnd = now + lease;
        await using SqlConnection connection = await OpenReadyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(_sql.Inbox.Claim, connection);
        command.Parameters.Add(MessageId(messageId));
        command.Parameters.Add(Time("@now", now));
        command.Parameters.Add(Time("@lease_until", leaseEnd));
        var claim = (MessageClaim)(int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new MessageClaimResult(claim, claim == MessageClaim.Claimed ? leaseEnd.Ticks : 0);
    }

    /// <inheritdoc />
    public async ValueTask CompleteMessageAsync(string messageId, long claimToken, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(messageId, nameof(messageId));
        await ExecuteAsync(_sql.Inbox.Complete, cancellationToken, MessageId(messageId), ClaimLeaseEnd(claimToken), Time("@now", Now())).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ReleaseMessageAsync(string messageId, long claimToken, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(messageId, nameof(messageId));
        await ExecuteAsync(_sql.Inbox.Release, cancellationToken, MessageId(messageId), ClaimLeaseEnd(claimToken)).ConfigureAwait(false);
    }

    // The claim a complete or release acts on, named by the lease end its token holds.
    private static SqlParameter ClaimLeaseEnd(long claimToken)
        => new SqlParameter("@lease_until", SqlDbType.DateTime2) { Scale = 7, Value = (object?)StoreSupport.InboxLeaseEnd(claimToken) ?? DBNull.Value };

    private static SqlParameter MessageId(string messageId) => new SqlParameter("@id", SqlDbType.NVarChar, JobLimits.MaxKeyLength) { Value = messageId };
}
