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
/// A command without <c>[Job]</c> is a job only when the serializer context covers it; otherwise it is
/// left out and enqueueing it fails, naming the missing attribute. The build reports an uncovered
/// <c>[Job]</c> command (ROGUE019) but cannot know which other commands are meant as jobs, so only
/// marked commands may fail startup: adding the package never breaks a project the build did not warn.
/// </remarks>
internal sealed class JobRegistry
{
    private readonly Dictionary<string, JobBinding> _byKind = new Dictionary<string, JobBinding>(StringComparer.Ordinal);
    private readonly Dictionary<Type, JobBinding> _byType = new Dictionary<Type, JobBinding>();
    private readonly HashSet<Type> _unbound = new HashSet<Type>();

    public JobRegistry(IEnumerable<JobDefinition> definitions, JobsOptions options)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;

        var uncovered = new List<string>();
        foreach (JobDefinition definition in definitions)
        {
            if (TryBind(definition, options))
            {
                continue;
            }

            // The generator passes JobDefaults.None exactly for a command without [Job].
            if (ReferenceEquals(definition.Defaults, JobDefaults.None))
            {
                _unbound.Add(definition.CommandType);
            }
            else if (options.SerializerContext is null)
            {
                throw new InvalidOperationException("Jobs are registered but JobsOptions.SerializerContext is not set. Set it in AddRogueJobs to a JsonSerializerContext with [JsonSerializable(typeof(T))] for each job command.");
            }
            else
            {
                uncovered.Add(definition.CommandType.ToString());
            }
        }

        if (uncovered.Count > 0)
        {
            throw new InvalidOperationException("The serializer context " + options.SerializerContext!.GetType() + " does not cover these job commands: " + string.Join(", ", uncovered) + ". Add [JsonSerializable(typeof(T))] for each.");
        }

        Queues = ResolveQueues(options);
    }

    public JobsOptions Options { get; }

    /// <summary>The queues the worker processes.</summary>
    public IReadOnlyList<string> Queues { get; }

    public bool TryGet(string kind, [NotNullWhen(true)] out JobBinding? binding) => _byKind.TryGetValue(kind, out binding);

    /// <exception cref="InvalidOperationException"><typeparamref name="TCommand"/> is not a job.</exception>
    public JobBinding<TCommand> Get<TCommand>()
        where TCommand : notnull
    {
        if (_byType.TryGetValue(typeof(TCommand), out JobBinding? binding) && binding is JobBinding<TCommand> typed)
        {
            return typed;
        }
        if (_unbound.Contains(typeof(TCommand)))
        {
            throw new InvalidOperationException(typeof(TCommand) + " cannot be enqueued: JobsOptions.SerializerContext does not cover it. Add [JsonSerializable(typeof(" + typeof(TCommand).Name + "))] to the context.");
        }
        throw new InvalidOperationException(typeof(TCommand) + " is not a job. A job is a command with a handler in a project that references SkathIO.Rogue.Jobs; enqueue it by its own type.");
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
