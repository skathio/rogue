using System;

namespace SkathIO.Rogue.Messaging;

/// <summary>What a transport does natively. The consumer host fills in what a transport lacks.</summary>
[Flags]
public enum TransportCapabilities
{
    /// <summary>None of the capabilities below.</summary>
    None = 0,

    /// <summary>
    /// The transport holds a message nacked with a delay until the delay has passed. Without it, the
    /// consumer waits out a retry delay itself, holding one of its concurrency slots, and then nacks
    /// with no delay.
    /// </summary>
    NativeDelay = 1,

    /// <summary>
    /// A dead-lettered message is kept by the transport (a dead-letter queue, for one) where it can be
    /// inspected and replayed. Without it, a dead-lettered message is dropped, and the consumer's error
    /// log is its only record.
    /// </summary>
    NativeDeadLetter = 2,

    /// <summary>A batch passed to <see cref="IMessageTransport.SendAsync"/> goes in one round trip.</summary>
    BatchSend = 4,

    /// <summary>
    /// The transport holds a message deferred with a delay until the delay has passed, even when it
    /// lacks <see cref="NativeDelay"/> for nacked ones. The consumer then hands it the delay of each
    /// delivery an inbox turns away as busy instead of waiting it out, so such deliveries hold no place
    /// in its unsettled window; it still waits out the retry delay of a failed message itself.
    /// </summary>
    NativeDeferDelay = 8,
}
