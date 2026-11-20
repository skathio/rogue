using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// A job definition bound to its source-generated <see cref="JsonTypeInfo{T}"/> and to the options'
/// defaults: everything the scheduler and the worker need for one kind.
/// </summary>
internal abstract class JobBinding
{
    protected JobBinding(JobDefinition definition, JobsOptions options)
    {
        JobDefaults defaults = definition.Defaults;
        Kind = definition.Kind;
        CommandType = definition.CommandType;
        Queue = defaults.Queue ?? options.DefaultQueue;
        MaxAttempts = defaults.MaxAttempts > 0 ? defaults.MaxAttempts : options.DefaultMaxAttempts;
        Backoff = defaults.BackoffSeconds > 0 ? TimeSpan.FromSeconds(defaults.BackoffSeconds) : options.DefaultBackoff;
        Timeout = defaults.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(defaults.TimeoutSeconds) : null;
        UniqueKey = defaults.UniqueKey;
    }

    public string Kind { get; }

    public Type CommandType { get; }

    public string Queue { get; }

    public int MaxAttempts { get; }

    public TimeSpan Backoff { get; }

    public TimeSpan? Timeout { get; }

    /// <summary>The unique key every job of this kind is stored with, or null for none.</summary>
    public string? UniqueKey { get; }

    /// <summary>Reads the payload and sends the command.</summary>
    /// <exception cref="JobPayloadException">The payload cannot be read as the command type.</exception>
    public abstract ValueTask InvokeAsync(RogueDispatcher dispatcher, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}

internal sealed class JobBinding<TCommand> : JobBinding
    where TCommand : notnull
{
    private readonly JobDefinition<TCommand> _definition;
    private readonly JsonTypeInfo<TCommand> _typeInfo;

    public JobBinding(JobDefinition<TCommand> definition, JsonTypeInfo<TCommand> typeInfo, JobsOptions options)
        : base(definition, options)
    {
        _definition = definition;
        _typeInfo = typeInfo;
    }

    public byte[] Serialize(TCommand command) => JsonSerializer.SerializeToUtf8Bytes(command, _typeInfo);

    public override ValueTask InvokeAsync(RogueDispatcher dispatcher, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        TCommand? command;
        try
        {
            command = JsonSerializer.Deserialize(payload.Span, _typeInfo);
        }
        catch (JsonException ex)
        {
            throw new JobPayloadException(Kind, ex);
        }
        if (command is null)
        {
            throw new JobPayloadException(Kind, null);
        }
        return _definition.Invoke(dispatcher, command, cancellationToken);
    }
}

/// <summary>A stored payload that cannot be read as its kind's command; retrying cannot fix it.</summary>
internal sealed class JobPayloadException : Exception
{
    public JobPayloadException(string kind, Exception? inner)
        : base("The payload of a '" + kind + "' job cannot be read as its command.", inner)
    {
    }
}
