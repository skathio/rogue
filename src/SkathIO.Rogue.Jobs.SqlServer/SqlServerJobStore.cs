using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SkathIO.Rogue.Jobs.Stores;

namespace SkathIO.Rogue.Jobs.SqlServer;

/// <summary>
/// A durable <see cref="IJobStore"/> on SQL Server. A claim takes a batch in one statement under
/// <c>READPAST</c> and <c>UPDLOCK</c>, so workers never wait on each other's rows; a batch enqueue is
/// one statement over a table-valued parameter; schema migrations run under <c>sp_getapplock</c>.
/// </summary>
/// <remarks>
/// Time is the store's <see cref="TimeProvider"/>, sent with each command, not the database clock, so
/// every process sharing the database must keep its clock in sync. The store creates or upgrades its
/// schema before its first command unless <see cref="DatabaseJobsOptions.AutoMigrate"/> is off.
/// </remarks>
public sealed class SqlServerJobStore : IJobStore, IDatabaseJobStore
{
    private const string OrphanError = "The job's lease ran out on its last attempt; its worker stopped without finishing it.";

    private readonly string _connectionString;
    private readonly SqlServerJobsOptions _options;
    private readonly TimeProvider _time;
    private readonly SqlServerSql _sql;
    private readonly MigrationGate _migrations;

    /// <summary>Creates a store on the database <paramref name="connectionString"/> names, with the given options, on the system clock by default.</summary>
    /// <exception cref="ArgumentException">The connection string is empty, or an option is out of range.</exception>
    public SqlServerJobStore(string connectionString, SqlServerJobsOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _options = StoreSupport.OwnCopy(options, static o => o.Copy());
        _connectionString = connectionString;
        _time = timeProvider ?? TimeProvider.System;
        _sql = new SqlServerSql(_options.Schema);
        _migrations = new MigrationGate(_options.AutoMigrate, MigrateAsync);
    }

    internal SqlServerJobsOptions Options => _options;

    DatabaseJobsOptions IDatabaseJobStore.StoreOptions => _options;

    /// <inheritdoc />
    /// <remarks>The whole batch is one INSERT over a table-valued parameter.</remarks>
    public async ValueTask EnqueueAsync(IReadOnlyList<NewJob> jobs, CancellationToken cancellationToken)
    {
        StoreSupport.CheckBatch(jobs);
        if (jobs.Count > 0)
        {
            await ExecuteAsync(_sql.Insert, cancellationToken, SqlServerRows.Jobs("@jobs", _sql.JobRows, jobs), Time("@now", Now())).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>One batch in one transaction: the run ledger row and the job commit together.</remarks>
    public async ValueTask<Guid> EnqueueOnceAsync(NewJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        DateTime now = Now();
        await using SqlConnection connection = await OpenReadyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(_sql.EnqueueOnce, connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = job.Id });
        command.Parameters.Add(Text("@kind", job.Kind, JobLimits.MaxKindLength));
        command.Parameters.Add(Text("@queue", job.Queue, JobLimits.MaxQueueLength));
        command.Parameters.Add(new SqlParameter("@payload", SqlDbType.VarBinary, -1) { Value = SqlServerRows.Bytes(job.Payload) });
        command.Parameters.Add(Int("@max_attempts", job.MaxAttempts));
        command.Parameters.Add(Time("@run_at", job.RunAt?.UtcDateTime ?? now));
        command.Parameters.Add(NullableText("@unique_key", job.UniqueKey, JobLimits.MaxKeyLength));
        command.Parameters.Add(NullableText("@task_key", job.TaskKey, JobLimits.MaxKeyLength));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ClaimedJob>> ClaimAsync(string queue, int maxCount, TimeSpan lease, CancellationToken cancellationToken)
    {
        StoreSupport.CheckClaim(queue, maxCount, lease);

        await using SqlConnection connection = await OpenReadyAsync(cancellationToken).ConfigureAwait(false);
        DateTime now = Now();
        await using var command = new SqlCommand(_sql.Claim, connection);
        command.Parameters.Add(Text("@queue", queue, JobLimits.MaxQueueLength));
        command.Parameters.Add(Time("@now", now));
        command.Parameters.Add(Time("@lease_until", now + lease));
        command.Parameters.Add(Int("@max_count", maxCount));
        command.Parameters.Add(Text("@orphan_error", OrphanError, JobLimits.MaxErrorLength));

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await StoreSupport.ReadClaimedAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<int> CompleteAsync(IReadOnlyList<JobLease> leases, CancellationToken cancellationToken)
        => UpdateLeasesAsync(_sql.Complete, leases, null, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<bool> RetryAsync(JobLease lease, TimeSpan delay, string error, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(error);
        DateTime now = Now();
        return await ExecuteAsync(_sql.Retry, cancellationToken, Held(lease, now, Time("@run_at", now + delay), Error(error))).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeadLetterAsync(JobLease lease, string error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        return await ExecuteAsync(_sql.DeadLetter, cancellationToken, Held(lease, Now(), Error(error))).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async ValueTask<bool> ReleaseAsync(JobLease lease, TimeSpan delay, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        DateTime now = Now();
        return await ExecuteAsync(_sql.Release, cancellationToken, Held(lease, now, Time("@run_at", now + delay))).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async ValueTask<int> RequeueAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return 0;
        }
        await using SqlConnection connection = await OpenReadyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(_sql.Requeue, connection);
        command.Parameters.Add(SqlServerRows.Ids("@ids", _sql.IdRows, new HashSet<Guid>(ids)));
        command.Parameters.Add(Time("@now", Now()));
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public ValueTask<int> ExtendLeasesAsync(IReadOnlyList<JobLease> leases, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        return UpdateLeasesAsync(_sql.Extend, leases, lease, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<JobSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenReadyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(_sql.Get, connection);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await StoreSupport.ReadSnapshotAsync(reader, id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryAcquireLeadershipAsync(string name, string owner, TimeSpan lease, CancellationToken cancellationToken)
    {
        StoreSupport.CheckLeadership(name, owner, lease);
        DateTime now = Now();
        await using SqlConnection connection = await OpenReadyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(_sql.AcquireLeadership, connection);
        command.Parameters.Add(Text("@name", name, JobLimits.MaxKeyLength));
        command.Parameters.Add(Text("@owner", owner, JobLimits.MaxKeyLength));
        command.Parameters.Add(Time("@now", now));
        command.Parameters.Add(Time("@lease_until", now + lease));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string;
    }

    /// <inheritdoc />
    public async ValueTask ReleaseLeadershipAsync(string name, string owner, CancellationToken cancellationToken)
    {
        StoreSupport.CheckLeadership(name, owner, null);
        await ExecuteAsync(_sql.ReleaseLeadership, cancellationToken, Text("@name", name, JobLimits.MaxKeyLength), Text("@owner", owner, JobLimits.MaxKeyLength)).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes succeeded and dead-lettered jobs older than their retention, a batch per statement, and
    /// returns how many it deleted. The pruner calls it every <see cref="DatabaseJobsOptions.PruneInterval"/>.
    /// </summary>
    public async Task<int> PruneAsync(CancellationToken cancellationToken = default)
    {
        DateTime now = Now();
        await ExecuteAsync(_sql.PruneRuns, cancellationToken, Time("@before", now - _options.SucceededRetention)).ConfigureAwait(false);
        return await StoreSupport.DeleteInBatchesAsync(ct => ExecuteAsync(
            _sql.Prune,
            ct,
            Time("@succeeded_before", now - _options.SucceededRetention),
            Time("@dead_lettered_before", now - _options.DeadLetteredRetention),
            Int("@batch", _options.PruneBatchSize)), _options.PruneBatchSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a job became due after <paramref name="since"/> and by <paramref name="now"/>; the poller's probe.</summary>
    internal async Task<bool> HasNewlyDueAsync(DateTime since, DateTime now, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenReadyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(_sql.Probe, connection);
        command.Parameters.Add(Time("@since", since));
        command.Parameters.Add(Time("@now", now));
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    /// <summary>
    /// Creates the schema, or applies the migrations it lacks, under an application lock so concurrent
    /// processes migrate once. Safe to call at any time; it does nothing when the schema is current.
    /// </summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await StoreSupport.InTransactionAsync<SqlTransaction>(connection, ApplyMigrationsAsync, cancellationToken).ConfigureAwait(false);
    }

    // Takes the migration lock and creates the schema, then runs each migration batch, which applies
    // itself only when the version table lacks it; all in the caller's transaction.
    private async Task ApplyMigrationsAsync(SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using (var prologue = new SqlCommand(_sql.MigrationPrologue, transaction.Connection, transaction))
        {
            prologue.Parameters.Add(Text("@resource", "SkathIO.Rogue.Jobs:" + _sql.Schema, 255));
            prologue.Parameters.Add(Text("@schema", _sql.Schema, 128));
            await prologue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (string migration in _sql.Migrations)
        {
            await using var step = new SqlCommand(migration, transaction.Connection, transaction);
            await step.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private static SqlParameter Time(string name, DateTime value) => new SqlParameter(name, SqlDbType.DateTime2) { Scale = 7, Value = value };

    private static SqlParameter Int(string name, int value) => new SqlParameter(name, SqlDbType.Int) { Value = value };

    private static SqlParameter Text(string name, string value, int size) => new SqlParameter(name, SqlDbType.NVarChar, size) { Value = value };

    private static SqlParameter NullableText(string name, string? value, int size) => new SqlParameter(name, SqlDbType.NVarChar, size) { Value = (object?)value ?? DBNull.Value };

    private static SqlParameter Error(string error) => Text("@error", JobLimits.TrimError(error), JobLimits.MaxErrorLength);

    // Runs a write over a list of leases, each lease once: completes them, or extends them to
    // `extendBy` from now. Returns how many claims still held their job.
    private async ValueTask<int> UpdateLeasesAsync(string sql, IReadOnlyList<JobLease> leases, TimeSpan? extendBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leases);
        if (leases.Count == 0)
        {
            return 0;
        }
        DateTime now = Now();
        SqlParameter rows = SqlServerRows.Leases("@leases", _sql.LeaseRows, new HashSet<JobLease>(leases));
        return extendBy is { } lease
            ? await ExecuteAsync(sql, cancellationToken, rows, Time("@now", now), Time("@lease_until", now + lease)).ConfigureAwait(false)
            : await ExecuteAsync(sql, cancellationToken, rows, Time("@now", now)).ConfigureAwait(false);
    }

    // The parameters of a write that applies only while the lease's claim still holds the job, at `now`,
    // followed by the write's own.
    private static SqlParameter[] Held(JobLease lease, DateTime now, params SqlParameter[] values)
    {
        var all = new SqlParameter[values.Length + 3];
        all[0] = new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = lease.Id };
        all[1] = Int("@attempt", lease.Attempt);
        all[2] = Time("@now", now);
        values.CopyTo(all, 3);
        return all;
    }

    // Runs one statement, once the schema is current, on a pooled connection of its own, and returns
    // the rows it changed.
    private async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken, params SqlParameter[] parameters)
    {
        await using SqlConnection connection = await OpenReadyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqlConnection> OpenReadyAsync(CancellationToken cancellationToken)
    {
        await _migrations.ReadyAsync(cancellationToken).ConfigureAwait(false);
        return await OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
