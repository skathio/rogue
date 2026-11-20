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

    public async ValueTask<Guid> Enqueue<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(command);
        JobBinding<TCommand> binding = _registry.Get<TCommand>();
        NewJob job = Create(binding, command);

        await _store.EnqueueAsync(new[] { job }, cancellationToken).ConfigureAwait(false);
        Enqueued(binding, 1);
        return job.Id;
    }

    public async ValueTask<IReadOnlyList<Guid>> EnqueueMany<TCommand>(IReadOnlyList<TCommand> commands, CancellationToken cancellationToken = default)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        JobBinding<TCommand> binding = _registry.Get<TCommand>();
        var jobs = new NewJob[commands.Count];
        var ids = new Guid[commands.Count];
        for (int i = 0; i < jobs.Length; i++)
        {
            TCommand command = commands[i] ?? throw new ArgumentException("The commands contain null at index " + i + ".", nameof(commands));
            jobs[i] = Create(binding, command);
            ids[i] = jobs[i].Id;
        }

        await _store.EnqueueAsync(jobs, cancellationToken).ConfigureAwait(false);
        Enqueued(binding, jobs.Length);
        return ids;
    }

    private NewJob Create<TCommand>(JobBinding<TCommand> binding, TCommand command)
        where TCommand : notnull
    {
        byte[] payload = binding.Serialize(command);
        int limit = _registry.Options.MaxPayloadBytes;
        if (payload.Length > limit)
        {
            throw new ArgumentException("The serialized '" + binding.Kind + "' command is " + payload.Length + " bytes; the limit is " + limit + " (JobsOptions.MaxPayloadBytes).", nameof(command));
        }
        return new NewJob
        {
            Id = NewId(),
            Kind = binding.Kind,
            Queue = binding.Queue,
            Payload = payload,
            MaxAttempts = binding.MaxAttempts,
        };
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
