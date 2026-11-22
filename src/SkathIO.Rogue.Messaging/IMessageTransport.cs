using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Messaging;

/// <summary>
/// Moves envelopes between services. The bus sends through it, and the consumer host receives from it
/// and settles each delivery exactly once: ack, nack with a delay, or dead-letter. A delivery that is
/// never settled (its process stopped) is delivered again.
/// </summary>
public interface IMessageTransport
{
    /// <summary>What the transport does natively; the consumer host fills in the rest.</summary>
    TransportCapabilities Capabilities { get; }

    /// <summary>
    /// The name of the service whose messages this transport receives, or null, the default, when it
    /// names none. An event published through <see cref="IPublisher"/> runs its local handlers there and
    /// then; the bus stamps this name on it as <see cref="MessageEnvelope.Origin"/>, and the consumer
    /// drops a received event that carries it, so those handlers do not run a second time.
    /// </summary>
    string? ServiceName => null;

    /// <summary>
    /// How many deliveries the transport hands out unsettled at most before it sends no more, or null,
    /// the default, when it sets no such limit. Without <see cref="TransportCapabilities.NativeDelay"/>
    /// the consumer waits out a retry delay holding the delivery unsettled; it lets at most half this
    /// many wait, and nacks any other failed delivery at once, its delivery count still one higher, so
    /// failing messages never take the whole window from the rest. Deliveries an inbox turns away as
    /// busy wait within the same half, and any other is deferred at once, unless the transport has
    /// <see cref="TransportCapabilities.NativeDeferDelay"/>: then none of them waits, and each is
    /// deferred with its delay.
    /// </summary>
    int? UnsettledLimit => null;

    /// <summary>Sends <paramref name="messages"/>, in order. An empty batch sends nothing.</summary>
    ValueTask SendAsync(IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the deliveries ready now, at most <paramref name="maxCount"/>, or an empty list when none
    /// is. A transport may wait briefly for the first one.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCount"/> is less than 1.</exception>
    ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxCount, CancellationToken cancellationToken);

    /// <summary>Settles <paramref name="message"/> as handled; it is not delivered again.</summary>
    ValueTask AckAsync(ReceivedMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Settles <paramref name="message"/> as not handled; it is delivered again, with its delivery count
    /// one higher, once <paramref name="delay"/> has passed (at once, without
    /// <see cref="TransportCapabilities.NativeDelay"/>).
    /// </summary>
    ValueTask NackAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken);

    /// <summary>
    /// Settles <paramref name="message"/> as not attempted: its handlers did not run, since another
    /// delivery holds its id, or were stopped because the consumer is shutting down. It is delivered again, with the same delivery count, once
    /// <paramref name="delay"/> has passed (at once, without <see cref="TransportCapabilities.NativeDelay"/>
    /// or <see cref="TransportCapabilities.NativeDeferDelay"/>), so being put back never uses up one of its attempts.
    /// </summary>
    ValueTask DeferAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken);

    /// <summary>
    /// Settles <paramref name="message"/> as one that cannot be handled; it is not delivered again, and
    /// is kept with <paramref name="reason"/> under <see cref="TransportCapabilities.NativeDeadLetter"/>.
    /// </summary>
    ValueTask DeadLetterAsync(ReceivedMessage message, string reason, CancellationToken cancellationToken);
}
