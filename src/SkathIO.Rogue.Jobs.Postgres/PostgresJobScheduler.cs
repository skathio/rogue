using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace SkathIO.Rogue.Jobs.Postgres;

internal sealed class PostgresJobScheduler : IPostgresJobScheduler
{
    private readonly IJobScheduler _scheduler;
    private readonly PostgresJobStore _store;

    public PostgresJobScheduler(IJobScheduler scheduler, PostgresJobStore store)
    {
        _scheduler = scheduler;
        _store = store;
    }

    public ValueTask<Guid> Enqueue<TCommand>(TCommand command, NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
        where TCommand : notnull
    {
        NpgsqlConnection connection = PostgresJobStore.ConnectionOf(transaction);
        return Enqueue(command, connection, cancellationToken);
    }

    public async ValueTask<Guid> Enqueue<TCommand>(TCommand command, NpgsqlConnection connection, CancellationToken cancellationToken = default)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(connection);
        NewJob job = _scheduler.CreateJob(command);
        if (job.UniqueKey is not null)
        {
            return await _store.EnqueueOnceAsync(job, connection, cancellationToken).ConfigureAwait(false);
        }
        await _store.EnqueueAsync(new[] { job }, connection, cancellationToken).ConfigureAwait(false);
        return job.Id;
    }

    public ValueTask<IReadOnlyList<Guid>> EnqueueMany<TCommand>(IReadOnlyList<TCommand> commands, NpgsqlTransaction transaction, CancellationToken cancellationToken = default)
        where TCommand : notnull
    {
        NpgsqlConnection connection = PostgresJobStore.ConnectionOf(transaction);
        return EnqueueMany(commands, connection, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<Guid>> EnqueueMany<TCommand>(IReadOnlyList<TCommand> commands, NpgsqlConnection connection, CancellationToken cancellationToken = default)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(connection);
        IReadOnlyList<NewJob> jobs = _scheduler.CreateJobs(commands);
        if (jobs.Count > 0 && jobs[0].UniqueKey is not null)
        {
            return await EnqueueEachAsync(jobs, connection, cancellationToken).ConfigureAwait(false);
        }
        await _store.EnqueueAsync(jobs, connection, cancellationToken).ConfigureAwait(false);
        var ids = new Guid[jobs.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = jobs[i].Id;
        }
        return ids;
    }

    // A unique key is held by one job at a time, so each job is stored on its own and gets the id of
    // the job that holds the key.
    private async ValueTask<IReadOnlyList<Guid>> EnqueueEachAsync(IReadOnlyList<NewJob> jobs, NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var ids = new Guid[jobs.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = await _store.EnqueueOnceAsync(jobs[i], connection, cancellationToken).ConfigureAwait(false);
        }
        return ids;
    }
}
