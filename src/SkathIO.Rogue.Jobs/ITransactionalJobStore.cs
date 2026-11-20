using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// A job store in a database that can store jobs inside the caller's own transaction, so the jobs
/// commit or roll back with the caller's data. The database stores implement it; an outbox enqueues
/// through it.
/// </summary>
public interface ITransactionalJobStore : IJobStore
{
    /// <summary>
    /// Stores <paramref name="jobs"/> inside <paramref name="transaction"/>: workers see them only once
    /// it commits, and never if it rolls back. Ids and batch rules are those of
    /// <see cref="IJobStore.EnqueueAsync"/>. The transaction must belong to this store's database
    /// client and be open on the database the store uses.
    /// </summary>
    /// <exception cref="ArgumentException">The transaction is not one of this store's database client, or a job carries a key.</exception>
    /// <exception cref="InvalidOperationException">The transaction has already been committed or rolled back.</exception>
    ValueTask EnqueueAsync(IReadOnlyList<NewJob> jobs, DbTransaction transaction, CancellationToken cancellationToken);
}
