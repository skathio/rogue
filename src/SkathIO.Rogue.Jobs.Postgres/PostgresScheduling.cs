using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace SkathIO.Rogue.Jobs.Postgres;

/// <summary>
/// The store's keyed enqueue and leader lock: what scheduling adds to the Postgres store. The store
/// checks arguments and runs migrations before calling in.
/// </summary>
internal sealed class PostgresScheduling
{
    // How many times a keyed insert is tried when the pending job holding its unique key finishes
    // between the insert that saw it and the lookup of its id.
    private const int UniqueKeyTries = 3;

    private readonly PostgresJobStore _store;
    private readonly PostgresSchedulingSql _sql;

    public PostgresScheduling(PostgresJobStore store)
    {
        _store = store;
        _sql = store.Sql.Scheduling;
    }

    /// <summary>
    /// Stores a keyed job on the connection, in its current transaction if any: first the recurring
    /// run, whose ledger row names the job stored for it; then the job, unless a pending job of its
    /// kind holds its unique key. Returns the id of the job that stands for it.
    /// </summary>
    public async Task<Guid> EnqueueOnceAsync(NewJob job, NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var write = new KeyedWrite(this, job, connection, cancellationToken);
        if (job.TaskKey is not null && await write.RunAsync(_sql.ClaimRun, job.Id).ConfigureAwait(false) is null)
        {
            return (await write.RunAsync(_sql.FindRun, null).ConfigureAwait(false))!.Value;
        }

        for (int tries = 1; ; tries++)
        {
            if (await write.InsertAsync().ConfigureAwait(false))
            {
                await _store.NotifyAsync(new[] { job }, connection, cancellationToken).ConfigureAwait(false);
                return job.Id;
            }
            if (await write.FindHolderAsync().ConfigureAwait(false) is { } holder)
            {
                if (job.TaskKey is not null)
                {
                    await write.RunAsync(_sql.PointRun, holder).ConfigureAwait(false);
                }
                return holder;
            }
            if (tries == UniqueKeyTries)
            {
                throw new InvalidOperationException("The job could not be stored: the job holding its unique key kept changing.");
            }
        }
    }

    public async Task<bool> TryAcquireLeadershipAsync(string name, string owner, TimeSpan lease, CancellationToken cancellationToken)
    {
        DateTime now = _store.Now();
        await using NpgsqlCommand command = LockCommand(_sql.AcquireLeadership, name, owner);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("lease_until", now + lease);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string;
    }

    public async Task ReleaseLeadershipAsync(string name, string owner, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = LockCommand(_sql.ReleaseLeadership, name, owner);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forgets the recurring runs due before <paramref name="before"/>; the scheduler never stores a run in the past.</summary>
    public async Task ForgetRunsBeforeAsync(DateTime before, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _store.DataSource.CreateCommand(_sql.PruneRuns);
        command.Parameters.AddWithValue("before", before);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private NpgsqlCommand LockCommand(string sql, string name, string owner)
    {
        NpgsqlCommand command = _store.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("owner", owner);
        return command;
    }

    /// <summary>The statements of one keyed enqueue, on one connection.</summary>
    private sealed class KeyedWrite
    {
        private readonly PostgresSchedulingSql _sql;
        private readonly NewJob _job;
        private readonly DateTime _runAt;
        private readonly NpgsqlConnection _connection;
        private readonly CancellationToken _cancellationToken;

        public KeyedWrite(PostgresScheduling owner, NewJob job, NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            _sql = owner._sql;
            _job = job;
            _runAt = PostgresJobStore.RunAt(job, owner._store.Now());
            _connection = connection;
            _cancellationToken = cancellationToken;
        }

        /// <summary>Runs a statement on the job's ledger row, writing <paramref name="id"/> if given; returns the job id it returned, if any.</summary>
        public async Task<Guid?> RunAsync(string sql, Guid? id)
        {
            await using var command = new NpgsqlCommand(sql, _connection);
            command.Parameters.AddWithValue("task_key", _job.TaskKey!);
            command.Parameters.AddWithValue("run_at", _runAt);
            if (id is { } value)
            {
                command.Parameters.AddWithValue("id", value);
            }
            return await command.ExecuteScalarAsync(_cancellationToken).ConfigureAwait(false) as Guid?;
        }

        /// <summary>Inserts the job; false when a pending job of its kind holds its unique key.</summary>
        public async Task<bool> InsertAsync()
        {
            await using var insert = new NpgsqlCommand(_sql.InsertUnique, _connection);
            PostgresJobStore.AddJobValues(insert.Parameters, _job, _runAt);
            insert.Parameters.Add(new NpgsqlParameter("unique_key", NpgsqlDbType.Text) { Value = (object?)_job.UniqueKey ?? DBNull.Value });
            return await insert.ExecuteScalarAsync(_cancellationToken).ConfigureAwait(false) is Guid;
        }

        /// <summary>The id of the pending job holding the unique key, or null when none holds it now.</summary>
        public async Task<Guid?> FindHolderAsync()
        {
            await using var find = new NpgsqlCommand(_sql.FindUnique, _connection);
            find.Parameters.AddWithValue("kind", _job.Kind);
            find.Parameters.AddWithValue("unique_key", _job.UniqueKey!);
            return await find.ExecuteScalarAsync(_cancellationToken).ConfigureAwait(false) as Guid?;
        }
    }
}
