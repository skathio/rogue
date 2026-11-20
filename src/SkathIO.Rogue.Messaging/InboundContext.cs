using System.Threading;

namespace SkathIO.Rogue.Messaging;

/// <summary>
/// The inbound message whose handlers are running on this async flow, if any. Messages published from
/// those handlers take their correlation and causation ids from it, and the message itself is not sent
/// back to the bus when its handlers are run through <see cref="IPublisher"/>.
/// </summary>
internal sealed class InboundContext
{
    private static readonly AsyncLocal<InboundContext?> _current = new AsyncLocal<InboundContext?>();

    private InboundContext(MessageEnvelope envelope, object message)
    {
        Envelope = envelope;
        Message = message;
    }

    public static InboundContext? Current => _current.Value;

    public MessageEnvelope Envelope { get; }

    public object Message { get; }

    /// <summary>
    /// Makes the message current for the rest of the calling async method and the code it awaits. An
    /// async method's changes to an <see cref="AsyncLocal{T}"/> are undone when it returns, so the caller
    /// must be the async method that dispatches the message.
    /// </summary>
    public static void Enter(MessageEnvelope envelope, object message) => _current.Value = new InboundContext(envelope, message);
}
