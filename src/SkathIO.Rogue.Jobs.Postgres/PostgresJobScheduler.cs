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
        var jobs = new NewJob[commands.Count];
        var ids = new Guid[commands.Count];
        for (int i = 0; i < jobs.Length; i++)
        {
            TCommand command = commands[i] ?? throw new ArgumentException("The commands contain null at index " + i + ".", nameof(commands));
            jobs[i] = _scheduler.CreateJob(command);
            ids[i] = jobs[i].Id;
        }
        await _store.EnqueueAsync(jobs, connection, cancellationToken).ConfigureAwait(false);
        return ids;
    }
}
