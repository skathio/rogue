using System;

namespace SkathIO.Rogue.Messaging;

/// <summary>
/// A message as it travels: its body and the headers every transport carries with it. The bus fills
/// every header when it publishes; an envelope received from a transport is untrusted input.
/// </summary>
public sealed class MessageEnvelope
{
    /// <summary>The JSON content type, which the bus stamps on every message it publishes.</summary>
    public const string JsonContentType = "application/json";

    /// <summary>The message's unique id. A new envelope gets a fresh one.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The message's stable name, from its <see cref="MessageAttribute"/>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The body's media type; the consumer reads only <see cref="JsonContentType"/>.</summary>
    public string ContentType { get; set; } = JsonContentType;

    /// <summary>The id shared by every message of one conversation: the first message's id.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>The id of the message whose handler published this one, or null for none.</summary>
    public string? CausationId { get; set; }

    /// <summary>The W3C <c>traceparent</c> of the activity that published the message, or null for none.</summary>
    public string? TraceParent { get; set; }

    /// <summary>
    /// The <see cref="IMessageTransport.ServiceName"/> of the service that published this event through
    /// <see cref="IPublisher"/>, where its local handlers already ran; null for any other message. That
    /// service's consumer drops the event when its own copy comes back, so its handlers run once. The
    /// sender sets it, so a forged one can only keep that service from handling the sender's own events.
    /// </summary>
    public string? Origin { get; set; }

    /// <summary>The serialized message.</summary>
    public ReadOnlyMemory<byte> Body { get; set; }
}
