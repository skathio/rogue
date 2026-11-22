using System;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Messaging;

/// <summary>
/// One message type: its stable name and the typed dispatcher that hands it to its handlers. The
/// source generator emits one per <see cref="MessageAttribute"/> type in a compilation that references
/// this package and registers it with the service collection; inbound messages are looked up by
/// <see cref="Name"/>, never by a type name.
/// </summary>
public abstract class MessageBinding
{
    private protected MessageBinding(string name)
    {
        MessageLimits.CheckName(name, nameof(name));
        Name = name;
    }

    /// <summary>The stable name the message travels under.</summary>
    public string Name { get; }

    /// <summary>The message type.</summary>
    public abstract Type MessageType { get; }

    /// <summary>
    /// Whether a message that arrives is handed to this process's handlers (it is an event or a
    /// command), or the type is only sent from here. A transport subscribes its consumer to the names
    /// of the bindings that can dispatch.
    /// </summary>
    public abstract bool CanDispatch { get; }

    /// <summary>
    /// Completes when <paramref name="pending"/> does, dropping its result. Generated dispatchers use it
    /// for commands that return a value, so a synchronously completed send stays allocation-free.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
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

    // Null when the serializer context does not cover the message type.
    internal abstract BoundMessage? Bind(JsonSerializerContext context);
}

/// <summary>The message binding of <typeparamref name="TMessage"/>.</summary>
/// <typeparam name="TMessage">The message type.</typeparam>
public sealed class MessageBinding<TMessage> : MessageBinding
    where TMessage : notnull
{
    private readonly Func<IServiceProvider, TMessage, CancellationToken, ValueTask>? _dispatch;

    /// <summary>Creates the binding; generated code calls this once per message type.</summary>
    /// <param name="name">The stable name the message travels under.</param>
    /// <param name="dispatch">
    /// Hands the message to its handlers through the dispatcher of the given scope, or null when this
    /// process cannot handle the type (it is then only sent).
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid message name.</exception>
    public MessageBinding(string name, Func<IServiceProvider, TMessage, CancellationToken, ValueTask>? dispatch)
        : base(name)
    {
        _dispatch = dispatch;
    }

    /// <inheritdoc />
    public override Type MessageType => typeof(TMessage);

    /// <inheritdoc />
    public override bool CanDispatch => _dispatch is not null;

    internal override BoundMessage? Bind(JsonSerializerContext context)
        => context.GetTypeInfo(typeof(TMessage)) is JsonTypeInfo<TMessage> typeInfo
            ? new BoundMessage<TMessage>(Name, typeInfo, _dispatch)
            : null;
}

/// <summary>A message binding bound to its source-generated <see cref="JsonTypeInfo{T}"/>.</summary>
internal abstract class BoundMessage
{
    protected BoundMessage(string name) => Name = name;

    public string Name { get; }

    /// <summary>Whether this process has handlers for the message, or only sends it.</summary>
    public abstract bool CanDispatch { get; }

    public abstract byte[] Serialize(object message);

    /// <summary>
    /// Reads <paramref name="body"/> as the message, refusing JSON nested deeper than
    /// <paramref name="maxDepth"/> or followed by anything but white space.
    /// </summary>
    /// <exception cref="JsonException">The body is not one JSON value of the message type within the depth.</exception>
    public abstract object Deserialize(ReadOnlyMemory<byte> body, int maxDepth);

    public abstract ValueTask DispatchAsync(IServiceProvider services, object message, CancellationToken cancellationToken);
}

internal sealed class BoundMessage<TMessage> : BoundMessage
    where TMessage : notnull
{
    private readonly JsonTypeInfo<TMessage> _typeInfo;
    private readonly Func<IServiceProvider, TMessage, CancellationToken, ValueTask>? _dispatch;

    public BoundMessage(string name, JsonTypeInfo<TMessage> typeInfo, Func<IServiceProvider, TMessage, CancellationToken, ValueTask>? dispatch)
        : base(name)
    {
        _typeInfo = typeInfo;
        _dispatch = dispatch;
    }

    public override bool CanDispatch => _dispatch is not null;

    public override byte[] Serialize(object message) => JsonSerializer.SerializeToUtf8Bytes((TMessage)message, _typeInfo);

    public override object Deserialize(ReadOnlyMemory<byte> body, int maxDepth)
    {
        // The reader enforces the depth while it reads, so a deep body costs no more than the limit
        // allows; the serializer options' own MaxDepth, set by the context, is not relied on.
        var reader = new Utf8JsonReader(body.Span, new JsonReaderOptions { MaxDepth = maxDepth });
        TMessage? message = JsonSerializer.Deserialize(ref reader, _typeInfo);
        if (reader.Read())
        {
            throw new JsonException("The body has more than one JSON value.");
        }
        return message ?? throw new JsonException("The body is JSON null.");
    }

    public override ValueTask DispatchAsync(IServiceProvider services, object message, CancellationToken cancellationToken)
        => _dispatch!(services, (TMessage)message, cancellationToken);
}
