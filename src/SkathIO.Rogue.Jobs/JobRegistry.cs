using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// Every job kind known to this process, bound to its serializer. Built once from the generated
/// definitions; it fails at startup, naming each command, when a kind is taken twice or a
/// <c>[Job]</c> command is not covered by the serializer context.
/// </summary>
/// <remarks>
/// Only a command marked <c>[Job]</c> is a job. The generator also emits a definition for every other
/// command, with <see cref="JobDefaults.None"/>; those are never bound, so a stored row naming one is
/// never dispatched (anyone who can write to the jobs table could otherwise send any command), and
/// enqueueing one fails, naming the missing attribute. The build-time serializer check (ROGUE019)
/// follows the same rule and checks only <c>[Job]</c> commands.
/// </remarks>
internal sealed class JobRegistry
{
    private readonly Dictionary<string, JobBinding> _byKind = new Dictionary<string, JobBinding>(StringComparer.Ordinal);
    private readonly Dictionary<Type, JobBinding> _byType = new Dictionary<Type, JobBinding>();
    private readonly HashSet<Type> _unmarked = new HashSet<Type>();

    public JobRegistry(IEnumerable<JobDefinition> definitions, JobsOptions options, IEnumerable<RecurringJob>? recurring = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;

        var uncovered = new List<string>();
        foreach (JobDefinition definition in definitions)
        {
            // The generator passes JobDefaults.None exactly for a command without [Job].
            if (ReferenceEquals(definition.Defaults, JobDefaults.None))
            {
                _unmarked.Add(definition.CommandType);
            }
            else if (!TryBind(definition, options))
            {
                if (options.SerializerContext is null)
                {
                    throw new InvalidOperationException("Jobs are registered but JobsOptions.SerializerContext is not set. Set it in AddRogueJobs to a JsonSerializerContext with [JsonSerializable(typeof(T))] for each job command.");
                }
                uncovered.Add(definition.CommandType.ToString());
            }
        }

        if (uncovered.Count > 0)
        {
            throw new InvalidOperationException("The serializer context " + options.SerializerContext!.GetType() + " does not cover these job commands: " + string.Join(", ", uncovered) + ". Add [JsonSerializable(typeof(T))] for each.");
        }

        Queues = ResolveQueues(options);
        Recurring = BindRecurring(recurring ?? Array.Empty<RecurringJob>());
    }

    public JobsOptions Options { get; }

    /// <summary>The queues the worker processes.</summary>
    public IReadOnlyList<string> Queues { get; }

    /// <summary>The recurring jobs, each bound to its command's job kind.</summary>
    public IReadOnlyList<RecurringBinding> Recurring { get; }

    public bool TryGet(string kind, [NotNullWhen(true)] out JobBinding? binding) => _byKind.TryGetValue(kind, out binding);

    /// <exception cref="InvalidOperationException"><typeparamref name="TCommand"/> is not a job.</exception>
    public JobBinding<TCommand> Get<TCommand>()
        where TCommand : notnull
    {
        if (_byType.TryGetValue(typeof(TCommand), out JobBinding? binding) && binding is JobBinding<TCommand> typed)
        {
            return typed;
        }
        if (_unmarked.Contains(typeof(TCommand)))
        {
            throw new InvalidOperationException(typeof(TCommand) + " is not a job: only commands marked [Job] run as jobs. Add [Job] to " + typeof(TCommand).Name + " and [JsonSerializable(typeof(" + typeof(TCommand).Name + "))] to JobsOptions.SerializerContext.");
        }
        throw new InvalidOperationException(typeof(TCommand) + " is not a job. A job is a command marked [Job] with a handler, in a project that references SkathIO.Rogue.Jobs; enqueue it by its own type.");
    }

    // Binds the definition to the serializer context; false when there is no context or it does not
    // cover the command. A kind already bound to the same command counts as bound.
    private bool TryBind(JobDefinition definition, JobsOptions options)
    {
        JsonSerializerContext? context = options.SerializerContext;
        if (context is null)
        {
            return false;
        }

        if (_byKind.TryGetValue(definition.Kind, out JobBinding? existing))
        {
            if (existing.CommandType != definition.CommandType)
            {
                throw new InvalidOperationException("The job kind '" + definition.Kind + "' is used by both " + existing.CommandType + " and " + definition.CommandType + ". Give one a different [Job(Kind = ...)].");
            }
            return true;
        }

        JobBinding? binding = definition.Bind(context, options);
        if (binding is null)
        {
            return false;
        }
        _byKind.Add(binding.Kind, binding);
        _byType.Add(binding.CommandType, binding);
        return true;
    }

    // A recurring job's command must be a job with a parameterless constructor, and has one schedule.
    private List<RecurringBinding> BindRecurring(IEnumerable<RecurringJob> recurring)
    {
        var bound = new List<RecurringBinding>();
        var seen = new HashSet<Type>();
        foreach (RecurringJob job in recurring)
        {
            if (!seen.Add(job.CommandType))
            {
                throw new InvalidOperationException("The command " + job.CommandType + " is registered as a recurring job more than once. Keep one schedule for it.");
            }
            bound.Add(job.Bind(this));
        }
        return bound;
    }

    private IReadOnlyList<string> ResolveQueues(JobsOptions options)
    {
        var queues = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (options.Queues.Count > 0)
        {
            foreach (string queue in options.Queues)
            {
                if (seen.Add(queue))
                {
                    queues.Add(queue);
                }
            }
            return queues;
        }

        seen.Add(options.DefaultQueue);
        queues.Add(options.DefaultQueue);
        foreach (JobBinding binding in _byKind.Values)
        {
            if (seen.Add(binding.Queue))
            {
                queues.Add(binding.Queue);
            }
        }
        return queues;
    }
}
