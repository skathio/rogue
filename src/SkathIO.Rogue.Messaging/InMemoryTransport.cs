using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Messaging;

/// <summary>
/// A transport that keeps messages in this process's memory: whatever is sent is received here. It
/// holds nacked messages for their delay on its clock and keeps dead-lettered ones in
/// <see cref="DeadLettered"/>. For tests and single-process development; messages are lost when the
/// process stops.
/// </summary>
/// <remarks>
/// Every message sent is delivered back to this process, so an event published through
/// <see cref="IPublisher"/> runs its local handlers again when the consumer receives it.
/// </remarks>
public sealed class InMemoryTransport : IMessageTransport
{
    private readonly object _gate = new object();
    private readonly TimeProvider _time;
    private readonly PriorityQueue<Entry, (DateTimeOffset Due, long Sequence)> _ready = new PriorityQueue<Entry, (DateTimeOffset, long)>();
    private readonly HashSet<Delivery> _inFlight = new HashSet<Delivery>();
    private readonly List<DeadLetteredMessage> _deadLettered = new List<DeadLetteredMessage>();
    private long _sequence;

    /// <summary>Creates an empty transport on <paramref name="timeProvider"/>'s clock, or the system clock.</summary>
    public InMemoryTransport(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public TransportCapabilities Capabilities => TransportCapabilities.NativeDelay | TransportCapabilities.NativeDeadLetter | TransportCapabilities.BatchSend;

    /// <summary>The messages dead-lettered so far, oldest first, each with its reason.</summary>
    public IReadOnlyList<DeadLetteredMessage> DeadLettered
    {
        get
        {
            lock (_gate)
            {
                return _deadLettered.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public ValueTask SendAsync(IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (MessageEnvelope envelope in messages)
        {
            ArgumentNullException.ThrowIfNull(envelope, nameof(messages));
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            foreach (MessageEnvelope envelope in messages)
            {
                Enqueue(new Entry(envelope, 1), now);
            }
        }
        return default;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        cancellationToken.ThrowIfCancellationRequested();
        var received = new List<ReceivedMessage>();
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            while (received.Count < maxCount && _ready.TryPeek(out Entry? entry, out (DateTimeOffset Due, long Sequence) at) && at.Due <= now)
            {
                _ready.Dequeue();
                var delivery = new Delivery(entry, this);
                _inFlight.Add(delivery);
                received.Add(delivery);
            }
        }
        return new ValueTask<IReadOnlyList<ReceivedMessage>>(received);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException"><paramref name="message"/> is already settled or was not delivered by this transport.</exception>
    public ValueTask AckAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Settle(message);
        }
        return default;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException"><paramref name="message"/> is already settled or was not delivered by this transport.</exception>
    public ValueTask NackAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        lock (_gate)
        {
            Entry entry = Settle(message);
            Enqueue(new Entry(entry.Envelope, entry.DeliveryCount + 1), _time.GetUtcNow() + delay);
        }
        return default;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException"><paramref name="message"/> is already settled or was not delivered by this transport.</exception>
    public ValueTask DeadLetterAsync(ReceivedMessage message, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reason);
        lock (_gate)
        {
            Entry entry = Settle(message);
            _deadLettered.Add(new DeadLetteredMessage(entry.Envelope, reason));
        }
        return default;
    }

    private void Enqueue(Entry entry, DateTimeOffset due) => _ready.Enqueue(entry, (due, _sequence++));

    private Entry Settle(ReceivedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is not Delivery delivery || delivery.Owner != this || !_inFlight.Remove(delivery))
        {
            throw new InvalidOperationException("The message is already settled, or was not delivered by this transport.");
        }
        return delivery.Entry;
    }

    private sealed record Entry(MessageEnvelope Envelope, int DeliveryCount);

    private sealed class Delivery : ReceivedMessage
    {
        public Delivery(Entry entry, InMemoryTransport owner)
            : base(entry.Envelope, entry.DeliveryCount)
        {
            Entry = entry;
            Owner = owner;
        }

        public Entry Entry { get; }

        public InMemoryTransport Owner { get; }
    }
}

/// <summary>A message an <see cref="InMemoryTransport"/> dead-lettered, and why.</summary>
/// <param name="Envelope">The message.</param>
/// <param name="Reason">Why it was dead-lettered.</param>
public sealed record DeadLetteredMessage(MessageEnvelope Envelope, string Reason);
