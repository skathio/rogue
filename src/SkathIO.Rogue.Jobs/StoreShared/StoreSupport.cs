using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SkathIO.Rogue.Jobs.Stores;

/// <summary>The small steps every database store takes the same way.</summary>
/// <remarks>Shared source: each database store package compiles its own internal copy.</remarks>
internal static class StoreSupport
{
    /// <summary>
    /// Reads the rows a claim returned, as id, kind, queue, payload, attempt, max attempts, run time
    /// and sequence, and orders them as the store contract hands them out: by run time, then enqueue
    /// order. A database returns the rows a claim updated in no particular order.
    /// </summary>
    public static async Task<ClaimedJob[]> ReadClaimedAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        var claimed = new List<(DateTime RunAt, long Seq, ClaimedJob Job)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var job = new ClaimedJob
            {
                Id = reader.GetGuid(0),
                Kind = reader.GetString(1),
                Queue = reader.GetString(2),
                Payload = reader.GetFieldValue<byte[]>(3),
                Attempt = reader.GetInt32(4),
                MaxAttempts = reader.GetInt32(5),
            };
            claimed.Add((reader.GetDateTime(6), reader.GetInt64(7), job));
        }
        claimed.Sort(static (a, b) => a.RunAt != b.RunAt ? a.RunAt.CompareTo(b.RunAt) : a.Seq.CompareTo(b.Seq));
        var result = new ClaimedJob[claimed.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = claimed[i].Job;
        }
        return result;
    }

    /// <summary>
    /// Reads the job <paramref name="id"/>'s row, as kind, queue, state (a small integer), attempt,
    /// max attempts, run time (UTC) and last error, or returns null when there is none.
    /// </summary>
    public static async Task<JobSnapshot?> ReadSnapshotAsync(DbDataReader reader, Guid id, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return new JobSnapshot
        {
            Id = id,
            Kind = reader.GetString(0),
            Queue = reader.GetString(1),
            State = (JobState)reader.GetInt16(2),
            Attempt = reader.GetInt32(3),
            MaxAttempts = reader.GetInt32(4),
            RunAt = new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero),
            LastError = reader.IsDBNull(6) ? null : reader.GetString(6),
        };
    }

    /// <summary>
    /// Checks a batch for the store contract: not null, no null job, and no job with a unique key or a
    /// task key, which only a single keyed enqueue may store.
    /// </summary>
    public static void CheckBatch(IReadOnlyList<NewJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        foreach (NewJob job in jobs)
        {
            ArgumentNullException.ThrowIfNull(job, nameof(jobs));
            if (job.UniqueKey is not null || job.TaskKey is not null)
            {
                throw new ArgumentException("A job with a unique key or a task key is stored with EnqueueOnceAsync, not in a batch.", nameof(jobs));
            }
        }
    }

    /// <summary>Checks a claim's queue, batch size and lease for the store contract.</summary>
    public static void CheckClaim(string queue, int maxCount, TimeSpan lease)
    {
        JobLimits.CheckQueue(queue, nameof(queue));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
    }

    /// <summary>Checks a leader lock's name, owner and lease for the store contract.</summary>
    public static void CheckLeadership(string name, string owner, TimeSpan? lease)
    {
        JobLimits.CheckKey(name, nameof(name));
        JobLimits.CheckKey(owner, nameof(owner));
        if (lease is { } length)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, TimeSpan.Zero, "lease");
        }
    }

    /// <summary>
    /// The caller's options, or the defaults, copied with <paramref name="copy"/> so later changes to
    /// the caller's object do not reach the store, and checked.
    /// </summary>
    public static TOptions OwnCopy<TOptions>(TOptions? options, Func<TOptions, TOptions> copy)
        where TOptions : DatabaseJobsOptions, new()
    {
        TOptions own = copy(options ?? new TOptions());
        own.Validate();
        return own;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction on <paramref name="connection"/> and commits it;
    /// a failure rolls it back. Takes ownership of the connection and disposes it.
    /// </summary>
    public static async Task InTransactionAsync<TTransaction>(DbConnection connection, Func<TTransaction, CancellationToken, Task> work, CancellationToken cancellationToken)
        where TTransaction : DbTransaction
    {
        await using (connection.ConfigureAwait(false))
        {
            var transaction = (TTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await work(transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Checks the options <paramref name="configure"/> sets, then registers the store
    /// <paramref name="create"/> makes from them as the job store, in place of any store registered
    /// before, with the pruner that deletes its finished jobs. Registering again adds nothing.
    /// </summary>
    public static void AddDatabaseStore<TStore, TOptions>(IServiceCollection services, Action<TOptions>? configure, Func<TOptions, IServiceProvider, TStore> create, string pruneLogCategory)
        where TStore : class, IDatabaseJobStore
        where TOptions : DatabaseJobsOptions, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new TOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => create(options, sp));
        services.Replace(ServiceDescriptor.Singleton<IJobStore>(static sp => sp.GetRequiredService<TStore>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JobPruner>(sp =>
        {
            TStore store = sp.GetRequiredService<TStore>();
            return new JobPruner(store.PruneAsync, store.StoreOptions.PruneInterval, sp.GetRequiredService<TimeProvider>(), Logger(sp, pruneLogCategory));
        }));
    }

    /// <summary>
    /// Runs <paramref name="deleteBatch"/> until it deletes fewer than <paramref name="batchSize"/>
    /// rows, and returns how many it deleted in all.
    /// </summary>
    public static async Task<int> DeleteInBatchesAsync(Func<CancellationToken, Task<int>> deleteBatch, int batchSize, CancellationToken cancellationToken)
    {
        int total = 0;
        while (true)
        {
            int deleted = await deleteBatch(cancellationToken).ConfigureAwait(false);
            total += deleted;
            if (deleted < batchSize)
            {
                return total;
            }
        }
    }

    /// <summary>A logger for <paramref name="category"/>, or a null logger when the host registered no logging.</summary>
    public static ILogger Logger(IServiceProvider services, string category)
        => (ILogger?)services.GetService<ILoggerFactory>()?.CreateLogger(category) ?? NullLogger.Instance;

    /// <summary>
    /// The lease end an inbox claim token names: a database store's token is the UTC ticks of the
    /// lease_until it wrote. A token no claim could have returned gives null, which matches no row.
    /// </summary>
    public static DateTime? InboxLeaseEnd(long claimToken)
        => claimToken > 0 && claimToken <= DateTime.MaxValue.Ticks ? new DateTime(claimToken, DateTimeKind.Utc) : null;
}
