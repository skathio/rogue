using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace SkathIO.Rogue.Jobs.Postgres;

/// <summary>
/// A durable <see cref="IJobStore"/> on PostgreSQL. Claims take a batch in one statement with
/// <c>FOR UPDATE SKIP LOCKED</c>, so workers never wait on each other's rows; a batch enqueue is one
/// binary COPY; each enqueue sends a NOTIFY that wakes idle workers once it commits. Jobs can be
/// enqueued on the caller's own connection and transaction, so they commit or roll back with the
/// caller's data.
/// </summary>
/// <remarks>
/// Time is the store's <see cref="TimeProvider"/>, sent with each command, not the database clock, so
/// every process sharing the database must keep its clock in sync. The store creates or upgrades its
/// schema before its first command unless <see cref="PostgresJobsOptions.AutoMigrate"/> is off.
/// </remarks>
public sealed class PostgresJobStore : IJobStore
{
    // Above this many jobs an enqueue streams them through binary COPY; below, one INSERT is cheaper.
    private const int CopyThreshold = 2;

    private const string OrphanError = "The job's lease ran out on its last attempt; its worker stopped without finishing it.";

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresJobsOptions _options;
    private readonly TimeProvider _time;
    private readonly PostgresSql _sql;
    private readonly CompletionBatcher _completions;
    private readonly object _gate = new object();
    private Task? _migration;

    /// <summary>Creates a store on <paramref name="dataSource"/> with the given options, on the system clock by default.</summary>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    public PostgresJobStore(NpgsqlDataSource dataSource, PostgresJobsOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _options = (options ?? new PostgresJobsOptions()).Copy();
        _options.Validate();
        _dataSource = dataSource;
        _time = timeProvider ?? TimeProvider.System;
        _sql = new PostgresSql(_options.Schema);
        _completions = new CompletionBatcher(CompleteBatchAsync);
    }

    internal PostgresSql Sql => _sql;

    internal PostgresJobsOptions Options => _options;

    internal NpgsqlDataSource DataSource => _dataSource;

    /// <summary>
    /// Creates the schema, or applies the migrations it lacks, under an advisory lock so concurrent
    /// processes migrate once. Safe to call at any time; it does nothing when the schema is current.
    /// </summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await using (var prologue = new NpgsqlCommand(_sql.MigrationPrologue, connection, transaction))
                {
                    prologue.Parameters.AddWithValue("schema", _sql.Schema);
                    await prologue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                int current;
                await using (var read = new NpgsqlCommand(_sql.CurrentVersion, connection, transaction))
                {
                    current = (int)(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                }

                for (int version = current + 1; version <= _sql.Migrations.Length; version++)
                {
                    await using (var migrate = new NpgsqlCommand(_sql.Migrations[version - 1], connection, transaction))
                    {
                        await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    await using (var record = new NpgsqlCommand(_sql.RecordMigration, connection, transaction))
                    {
                        record.Parameters.AddWithValue("version", version);
                        await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask EnqueueAsync(IReadOnlyList<NewJob> jobs, CancellationToken cancellationToken)
    {
        CheckJobs(jobs);
        if (jobs.Count == 0)
        {
            return;
        }
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await WriteJobsAsync(jobs, connection, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stores <paramref name="jobs"/> on the caller's open <paramref name="connection"/>, inside its
    /// current transaction if it has one: the jobs, and the NOTIFY that wakes workers, then commit or
    /// roll back with the caller's own writes. Ids and batch rules are those of
    /// <see cref="EnqueueAsync(IReadOnlyList{NewJob}, CancellationToken)"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection is not open.</exception>
    public async ValueTask EnqueueAsync(IReadOnlyList<NewJob> jobs, NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        CheckJobs(jobs);
        if (connection.FullState != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection must be open, and not busy with another command, to enqueue jobs on it.");
        }
        if (jobs.Count == 0)
        {
            return;
        }
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        await WriteJobsAsync(jobs, connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores <paramref name="jobs"/> inside the caller's <paramref name="transaction"/>: they become
    /// visible to workers, and wake them, only if it commits.
    /// </summary>
    /// <exception cref="InvalidOperationException">The transaction has already completed.</exception>
    public ValueTask EnqueueAsync(IReadOnlyList<NewJob> jobs, NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(jobs, ConnectionOf(transaction), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ClaimedJob>> ClaimAsync(string queue, int maxCount, TimeSpan lease, CancellationToken cancellationToken)
    {
        JobLimits.CheckQueue(queue, nameof(queue));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        await ReadyAsync(cancellationToken).ConfigureAwait(false);

        DateTime now = Now();
        await using var batch = _dataSource.CreateBatch();
        NpgsqlBatchCommand rescue = batch.CreateBatchCommand();
        rescue.CommandText = _sql.Rescue;
        rescue.Parameters.AddWithValue("queue", queue);
        rescue.Parameters.AddWithValue("now", now);
        rescue.Parameters.AddWithValue("orphan_error", OrphanError);
        batch.BatchCommands.Add(rescue);
        NpgsqlBatchCommand claim = batch.CreateBatchCommand();
        claim.CommandText = _sql.Claim;
        claim.Parameters.AddWithValue("queue", queue);
        claim.Parameters.AddWithValue("now", now);
        claim.Parameters.AddWithValue("lease_until", now + lease);
        claim.Parameters.AddWithValue("max_count", maxCount);
        batch.BatchCommands.Add(claim);

        var claimed = new List<(DateTime RunAt, long Seq, ClaimedJob Job)>();
        await using (NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
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
        }

        // RETURNING has no order; the contract hands jobs out by run time, then enqueue order.
        claimed.Sort(static (a, b) => a.RunAt != b.RunAt ? a.RunAt.CompareTo(b.RunAt) : a.Seq.CompareTo(b.Seq));
        var result = new ClaimedJob[claimed.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = claimed[i].Job;
        }
        return result;
    }

    /// <inheritdoc />
    /// <remarks>Completions from concurrent callers are written together in one statement.</remarks>
    public async ValueTask<int> CompleteAsync(IReadOnlyList<JobLease> leases, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leases);
        if (leases.Count == 0)
        {
            return 0;
        }
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        return await _completions.CompleteAsync(leases, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> RetryAsync(JobLease lease, TimeSpan delay, string error, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(error);
        string trimmed = JobLimits.TrimError(error);
        return await UpdateHeldAsync(_sql.Retry, lease, (values, now) =>
        {
            values.AddWithValue("run_at", now + delay);
            values.AddWithValue("error", trimmed);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<bool> DeadLetterAsync(JobLease lease, string error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        string trimmed = JobLimits.TrimError(error);
        return UpdateHeldAsync(_sql.DeadLetter, lease, (values, _) => values.AddWithValue("error", trimmed), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> ReleaseAsync(JobLease lease, CancellationToken cancellationToken)
        => UpdateHeldAsync(_sql.Release, lease, null, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<int> ExtendLeasesAsync(IReadOnlyList<JobLease> leases, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        if (leases.Count == 0)
        {
            return 0;
        }
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        DateTime now = Now();
        await using NpgsqlCommand command = _dataSource.CreateCommand(_sql.Extend);
        AddLeaseArrays(command, Distinct(leases));
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("lease_until", now + lease);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<JobSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = _dataSource.CreateCommand(_sql.Get);
        command.Parameters.AddWithValue("id", id);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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
    /// Deletes succeeded and dead-lettered jobs older than their retention, a batch per statement, and
    /// returns how many it deleted. The pruner calls it every <see cref="PostgresJobsOptions.PruneInterval"/>.
    /// </summary>
    public async Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _time.GetUtcNow();
        int total = 0;
        while (true)
        {
            await using NpgsqlCommand command = _dataSource.CreateCommand(_sql.Prune);
            command.Parameters.AddWithValue("succeeded_before", (now - _options.SucceededRetention).UtcDateTime);
            command.Parameters.AddWithValue("dead_lettered_before", (now - _options.DeadLetteredRetention).UtcDateTime);
            command.Parameters.AddWithValue("batch", _options.PruneBatchSize);
            int deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            total += deleted;
            if (deleted < _options.PruneBatchSize)
            {
                return total;
            }
        }
    }

    /// <summary>The transaction's connection; throws when the transaction has already completed.</summary>
    internal static NpgsqlConnection ConnectionOf(NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        try
        {
            // Npgsql keeps Connection set after a commit or rollback; reading IsolationLevel is its
            // public check, and it throws once the transaction has completed. Enqueueing on a completed
            // transaction would commit the jobs at once, apart from the caller's data.
            _ = transaction.IsolationLevel;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("The transaction has already been committed or rolled back.", ex);
        }
        return transaction.Connection ?? throw new InvalidOperationException("The transaction has already been committed or rolled back.");
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private static void CheckJobs(IReadOnlyList<NewJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        foreach (NewJob job in jobs)
        {
            ArgumentNullException.ThrowIfNull(job, nameof(jobs));
        }
    }

    // Runs the migrations once per store before its first command, when AutoMigrate is on. A failed
    // migration is retried by the next command instead of failing every later one.
    private Task ReadyAsync(CancellationToken cancellationToken)
    {
        if (!_options.AutoMigrate)
        {
            return Task.CompletedTask;
        }
        Task migration;
        lock (_gate)
        {
            if (_migration is null || _migration.IsFaulted || _migration.IsCanceled)
            {
                _migration = MigrateAsync(CancellationToken.None);
            }
            migration = _migration;
        }
        return migration.IsCompletedSuccessfully ? Task.CompletedTask : migration.WaitAsync(cancellationToken);
    }

    // Writes the jobs and one NOTIFY per queue on the connection, in its current transaction if any.
    // A single job is one INSERT; a batch is one binary COPY, which a duplicate id fails as a whole.
    private async Task WriteJobsAsync(IReadOnlyList<NewJob> jobs, NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        DateTime now = Now();
        if (jobs.Count < CopyThreshold)
        {
            NewJob job = jobs[0];
            await using var insert = new NpgsqlCommand(_sql.Insert, connection);
            insert.Parameters.AddWithValue("id", job.Id);
            insert.Parameters.AddWithValue("kind", job.Kind);
            insert.Parameters.AddWithValue("queue", job.Queue);
            insert.Parameters.Add(new NpgsqlParameter<ReadOnlyMemory<byte>>("payload", NpgsqlDbType.Bytea) { TypedValue = job.Payload });
            insert.Parameters.AddWithValue("max_attempts", job.MaxAttempts);
            insert.Parameters.AddWithValue("now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CopyAsync(jobs, connection, now, cancellationToken).ConfigureAwait(false);
        }
        await NotifyAsync(jobs, connection, cancellationToken).ConfigureAwait(false);
    }

    private async Task CopyAsync(IReadOnlyList<NewJob> jobs, NpgsqlConnection connection, DateTime now, CancellationToken cancellationToken)
    {
        NpgsqlBinaryImporter importer = await connection.BeginBinaryImportAsync(_sql.Copy, cancellationToken).ConfigureAwait(false);
        await using (importer.ConfigureAwait(false))
        {
            foreach (NewJob job in jobs)
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(job.Id, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(job.Kind, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(job.Queue, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(job.Payload, NpgsqlDbType.Bytea, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(job.MaxAttempts, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
            }
            await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task NotifyAsync(IReadOnlyList<NewJob> jobs, NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var queues = new HashSet<string>(StringComparer.Ordinal);
        await using var batch = new NpgsqlBatch(connection);
        foreach (NewJob job in jobs)
        {
            if (queues.Add(job.Queue))
            {
                var notify = new NpgsqlBatchCommand(_sql.Notify);
                notify.Parameters.AddWithValue("channel", _sql.Schema);
                notify.Parameters.AddWithValue("queue", job.Queue);
                batch.BatchCommands.Add(notify);
            }
        }
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HashSet<JobLease>> CompleteBatchAsync(IReadOnlyList<JobLease> leases)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(_sql.Complete);
        AddLeaseArrays(command, Distinct(leases));
        command.Parameters.AddWithValue("now", Now());
        var held = new HashSet<JobLease>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
        while (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            held.Add(new JobLease(reader.GetGuid(0), reader.GetInt32(1)));
        }
        return held;
    }

    // Runs a write that applies only while the lease's claim still holds the job; addValues adds the
    // statement's own parameters, given the time the statement uses as now.
    private async ValueTask<bool> UpdateHeldAsync(string sql, JobLease lease, Action<NpgsqlParameterCollection, DateTime>? addValues, CancellationToken cancellationToken)
    {
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        DateTime now = Now();
        await using NpgsqlCommand command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", lease.Id);
        command.Parameters.AddWithValue("attempt", lease.Attempt);
        command.Parameters.AddWithValue("now", now);
        addValues?.Invoke(command.Parameters, now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static List<JobLease> Distinct(IReadOnlyList<JobLease> leases) => new List<JobLease>(new HashSet<JobLease>(leases));

    private static void AddLeaseArrays(NpgsqlCommand command, List<JobLease> leases)
    {
        var ids = new Guid[leases.Count];
        var attempts = new int[leases.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = leases[i].Id;
            attempts[i] = leases[i].Attempt;
        }
        command.Parameters.AddWithValue("ids", ids);
        command.Parameters.AddWithValue("attempts", attempts);
    }
}
