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

    // Null when the serializer context does not cover the command type.
    internal abstract JobBinding? Bind(JsonSerializerContext context, JobsOptions options);
}

/// <summary>The job definition of the command <typeparamref name="TCommand"/>.</summary>
/// <typeparam name="TCommand">The command type.</typeparam>
public sealed class JobDefinition<TCommand> : JobDefinition
    where TCommand : notnull
{
    private readonly Func<RogueDispatcher, TCommand, CancellationToken, ValueTask> _invoke;

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

    /// <inheritdoc />
    public override Type CommandType => typeof(TCommand);

    internal ValueTask Invoke(RogueDispatcher dispatcher, TCommand command, CancellationToken cancellationToken)
        => _invoke(dispatcher, command, cancellationToken);

    internal override JobBinding? Bind(JsonSerializerContext context, JobsOptions options)
        => context.GetTypeInfo(typeof(TCommand)) is JsonTypeInfo<TCommand> typeInfo
            ? new JobBinding<TCommand>(this, typeInfo, options)
            : null;
}
