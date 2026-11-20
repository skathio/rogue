using System;

namespace SkathIO.Rogue.Messaging;

/// <summary>
/// A message a transport delivered and holds until it is settled: acked, nacked or dead-lettered.
/// A transport derives from it to keep its own delivery handle.
/// </summary>
public class ReceivedMessage
{
    /// <summary>Initializes the delivery.</summary>
    /// <param name="envelope">The delivered message.</param>
    /// <param name="deliveryCount">How many times the message has been delivered to be handled, this time included; at least 1.</param>
    public ReceivedMessage(MessageEnvelope envelope, int deliveryCount)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentOutOfRangeException.ThrowIfLessThan(deliveryCount, 1);
        Envelope = envelope;
        DeliveryCount = deliveryCount;
    }

    /// <summary>The delivered message.</summary>
    public MessageEnvelope Envelope { get; }

    /// <summary>
    /// How many times the message has been delivered to be handled, this time included; the consumer
    /// counts attempts by it. A delivery put back unhandled or stopped by shutdown, through
    /// <see cref="IMessageTransport.DeferAsync"/> is not counted.
    /// </summary>
    public int DeliveryCount { get; }
}
