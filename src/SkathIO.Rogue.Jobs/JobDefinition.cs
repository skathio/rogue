using System;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// One job kind: its stable name, its defaults and the typed invoker that sends the command through
/// the Rogue dispatcher. The source generator emits one per command in a compilation that references
/// this package and registers it with the service collection; the worker looks jobs up by
/// <see cref="Kind"/>, never by a type name.
/// </summary>
public abstract class JobDefinition
{
    private protected JobDefinition(string kind, JobDefaults defaults)
    {
        JobLimits.CheckKind(kind, nameof(kind));
        ArgumentNullException.ThrowIfNull(defaults);
        Kind = kind;
        Defaults = defaults;
    }

    /// <summary>The stable name the job is stored under.</summary>
    public string Kind { get; }

    /// <summary>The defaults from the command's <see cref="JobAttribute"/>.</summary>
    public JobDefaults Defaults { get; }

    /// <summary>The command type.</summary>
    public abstract Type CommandType { get; }

    /// <summary>
    /// Completes when <paramref name="pending"/> does, dropping its result. Generated invokers use it
    /// for commands that return a value, so a synchronously completed send stays allocation-free.
    /// </summary>
    public static ValueTask IgnoreResult<TResult>(ValueTask<TResult> pending)
    {
        if (pending.IsCompletedSuccessfully)
        {
            _ = pending.Result;
            return default;
        }
        return AwaitAsync(pending);

        static async ValueTask AwaitAsync(ValueTask<TResult> pending) => await pending.ConfigureAwait(false);
    }

    // Null when the definition has no type info of its own and the serializer context, if any, does
    // not cover the command type.
    internal abstract JobBinding? Bind(JsonSerializerContext? context, JobsOptions options);
}

/// <summary>The job definition of the command <typeparamref name="TCommand"/>.</summary>
/// <typeparam name="TCommand">The command type.</typeparam>
public sealed class JobDefinition<TCommand> : JobDefinition
    where TCommand : notnull
{
    private readonly Func<RogueDispatcher, TCommand, CancellationToken, ValueTask> _invoke;
    private readonly JsonTypeInfo<TCommand>? _typeInfo;

    /// <summary>Creates the definition; generated code calls this once per command.</summary>
    /// <param name="kind">The stable name the job is stored under.</param>
    /// <param name="defaults">The defaults from the command's <see cref="JobAttribute"/>.</param>
    /// <param name="invoke">Sends the command through the dispatcher of the job's scope.</param>
    public JobDefinition(string kind, JobDefaults defaults, Func<RogueDispatcher, TCommand, CancellationToken, ValueTask> invoke)
        : base(kind, defaults)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        _invoke = invoke;
    }

    /// <summary>
    /// Creates the definition of a job a package ships for its own use, such as a relay, whose command
    /// the application's <see cref="JobsOptions.SerializerContext"/> does not cover: it is serialized
    /// with <paramref name="typeInfo"/> instead, and is a job without <see cref="JobAttribute"/>.
    /// </summary>
    /// <param name="kind">The stable name the job is stored under.</param>
    /// <param name="defaults">The job's defaults; not <see cref="JobDefaults.None"/>, which marks a command that is not a job.</param>
    /// <param name="typeInfo">The source-generated serializer of the command.</param>
    /// <param name="invoke">Runs the job, given the dispatcher of the job's scope.</param>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not a valid kind, or <paramref name="defaults"/> is <see cref="JobDefaults.None"/>.</exception>
    public JobDefinition(string kind, JobDefaults defaults, JsonTypeInfo<TCommand> typeInfo, Func<RogueDispatcher, TCommand, CancellationToken, ValueTask> invoke)
        : this(kind, defaults, invoke)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (ReferenceEquals(defaults, JobDefaults.None))
        {
            throw new ArgumentException("A job with its own serializer is a job: give it defaults, not JobDefaults.None.", nameof(defaults));
        }
        _typeInfo = typeInfo;
    }

    /// <inheritdoc />
    public override Type CommandType => typeof(TCommand);

    internal ValueTask Invoke(RogueDispatcher dispatcher, TCommand command, CancellationToken cancellationToken)
        => _invoke(dispatcher, command, cancellationToken);

    internal override JobBinding? Bind(JsonSerializerContext? context, JobsOptions options)
        => (_typeInfo ?? context?.GetTypeInfo(typeof(TCommand)) as JsonTypeInfo<TCommand>) is { } typeInfo
            ? new JobBinding<TCommand>(this, typeInfo, options)
            : null;
}
