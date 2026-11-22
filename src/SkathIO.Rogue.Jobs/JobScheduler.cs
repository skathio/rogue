using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs;

internal sealed class JobScheduler : IJobScheduler
{
    private readonly JobRegistry _registry;
    private readonly IJobStore _store;
    private readonly JobSignal _signal;

    public JobScheduler(JobRegistry registry, IJobStore store, JobSignal signal)
    {
        _registry = registry;
        _store = store;
        _signal = signal;
    }

    /// <summary>The store jobs are enqueued in.</summary>
    internal IJobStore Store => _store;

    public ValueTask<Guid> Enqueue<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : notnull
        => StoreOneAsync(command, null, cancellationToken);

    public ValueTask<Guid> Schedule<TCommand>(TCommand command, DateTimeOffset at, CancellationToken cancellationToken = default)
        where TCommand : notnull
        => StoreOneAsync(command, at, cancellationToken);

    public async ValueTask<IReadOnlyList<Guid>> EnqueueMany<TCommand>(IReadOnlyList<TCommand> commands, CancellationToken cancellationToken = default)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        JobBinding<TCommand> binding = _registry.Get<TCommand>();
        NewJob[] jobs = CreateAll(binding, commands);
        if (binding.UniqueKey is not null)
        {
            return await StoreEachAsync(binding, jobs, cancellationToken).ConfigureAwait(false);
        }

        await _store.EnqueueAsync(jobs, cancellationToken).ConfigureAwait(false);
        Enqueued(binding, jobs.Length);
        return Array.ConvertAll(jobs, static j => j.Id);
    }

    public IReadOnlyList<NewJob> CreateJobs<TCommand>(IReadOnlyList<TCommand> commands)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(commands);
        return commands.Count == 0 ? Array.Empty<NewJob>() : CreateAll(_registry.Get<TCommand>(), commands);
    }

    public NewJob CreateJob<TCommand>(TCommand command)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(command);
        return Create(_registry.Get<TCommand>(), command, _registry.Options.MaxPayloadBytes, JobPlacement.Now);
    }

    /// <summary>
    /// Serializes <paramref name="command"/> into a job of <paramref name="binding"/>'s kind, carrying
    /// the kind's unique key, placed as <paramref name="placement"/> says.
    /// </summary>
    /// <exception cref="ArgumentException">The serialized command is larger than <paramref name="maxPayloadBytes"/>.</exception>
    internal static NewJob Create<TCommand>(JobBinding<TCommand> binding, TCommand command, int maxPayloadBytes, JobPlacement placement)
        where TCommand : notnull
    {
        byte[] payload = binding.Serialize(command);
        if (payload.Length > maxPayloadBytes)
        {
            throw new ArgumentException("The serialized '" + binding.Kind + "' command is " + payload.Length + " bytes; the limit is " + maxPayloadBytes + " (JobsOptions.MaxPayloadBytes).", nameof(command));
        }
        return new NewJob
        {
            Id = NewId(),
            Kind = binding.Kind,
            Queue = binding.Queue,
            Payload = payload,
            MaxAttempts = binding.MaxAttempts,
            RunAt = placement.RunAt,
            UniqueKey = binding.UniqueKey,
            TaskKey = placement.TaskKey,
        };
    }

    /// <summary>
    /// Stores one job and returns the id of the job that stands for it: its own, or, when a key it
    /// carries is already held, the id of the job that holds it.
    /// </summary>
    internal async ValueTask<Guid> StoreAsync(JobBinding binding, NewJob job, CancellationToken cancellationToken)
    {
        Guid stored;
        if (job.IsKeyed)
        {
            stored = await _store.EnqueueOnceAsync(job, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _store.EnqueueAsync(new[] { job }, cancellationToken).ConfigureAwait(false);
            stored = job.Id;
        }
        if (stored == job.Id)
        {
            Enqueued(binding, 1);
        }
        return stored;
    }

    // Async, so a refused command faults the returned task as the other enqueue paths do.
    private async ValueTask<Guid> StoreOneAsync<TCommand>(TCommand command, DateTimeOffset? runAt, CancellationToken cancellationToken)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(command);
        JobBinding<TCommand> binding = _registry.Get<TCommand>();
        NewJob job = Create(binding, command, _registry.Options.MaxPayloadBytes, new JobPlacement(runAt, null));
        return await StoreAsync(binding, job, cancellationToken).ConfigureAwait(false);
    }

    private NewJob[] CreateAll<TCommand>(JobBinding<TCommand> binding, IReadOnlyList<TCommand> commands)
        where TCommand : notnull
    {
        int limit = _registry.Options.MaxPayloadBytes;
        var jobs = new NewJob[commands.Count];
        for (int i = 0; i < jobs.Length; i++)
        {
            TCommand command = commands[i] ?? throw new ArgumentException("The commands contain null at index " + i + ".", nameof(commands));
            jobs[i] = Create(binding, command, limit, JobPlacement.Now);
        }
        return jobs;
    }

    // A unique key is held by one job at a time, so each job is stored on its own and gets the id of
    // the job that holds the key.
    private async ValueTask<IReadOnlyList<Guid>> StoreEachAsync(JobBinding binding, NewJob[] jobs, CancellationToken cancellationToken)
    {
        var ids = new Guid[jobs.Length];
        for (int i = 0; i < jobs.Length; i++)
        {
            ids[i] = await StoreAsync(binding, jobs[i], cancellationToken).ConfigureAwait(false);
        }
        return ids;
    }

    private void Enqueued(JobBinding binding, int count)
    {
        RogueJobsTelemetry.Enqueued.Add(count,
            new KeyValuePair<string, object?>(RogueJobsTelemetry.KindTag, binding.Kind),
            new KeyValuePair<string, object?>(RogueJobsTelemetry.QueueTag, binding.Queue));
        _signal.Pulse();
    }

    // Time-ordered ids keep a store's primary-key index append-mostly where the runtime offers them.
    private static Guid NewId()
    {
#if NET9_0_OR_GREATER
        return Guid.CreateVersion7();
#else
        return Guid.NewGuid();
#endif
    }
}

/// <summary>When a new job is due (null for when it is stored) and the recurring task it is a run of, if any.</summary>
internal readonly record struct JobPlacement(DateTimeOffset? RunAt, string? TaskKey)
{
    public static JobPlacement Now => default;
}
