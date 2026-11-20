using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>
/// A clock that moves only when a test moves it. A timer created from it (by <c>Task.Delay</c>) records
/// its due time and fires at once, so a test sees which delay the code waited without waiting.
/// </summary>
internal sealed class StepClock : TimeProvider
{
    private readonly object _gate = new object();
    private DateTimeOffset _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public ConcurrentQueue<TimeSpan> Waited { get; } = new ConcurrentQueue<TimeSpan>();

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Waited.Enqueue(dueTime);
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new FiredTimer();
    }

    private sealed class FiredTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}

/// <summary>An <see cref="InMemoryTransport"/> that reports the capabilities a test gives it and records each nack's delay.</summary>
internal sealed class RecordingTransport : IMessageTransport
{
    private readonly InMemoryTransport _inner;

    public RecordingTransport(InMemoryTransport inner, TransportCapabilities capabilities)
    {
        _inner = inner;
        Capabilities = capabilities;
    }

    public TransportCapabilities Capabilities { get; }

    public ConcurrentQueue<TimeSpan> NackDelays { get; } = new ConcurrentQueue<TimeSpan>();

    public InMemoryTransport Inner => _inner;

    public ValueTask SendAsync(IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken) => _inner.SendAsync(messages, cancellationToken);

    public ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxCount, CancellationToken cancellationToken) => _inner.ReceiveAsync(maxCount, cancellationToken);

    public ValueTask AckAsync(ReceivedMessage message, CancellationToken cancellationToken) => _inner.AckAsync(message, cancellationToken);

    public ValueTask NackAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken)
    {
        NackDelays.Enqueue(delay);
        return _inner.NackAsync(message, delay, cancellationToken);
    }

    public ValueTask DeadLetterAsync(ReceivedMessage message, string reason, CancellationToken cancellationToken) => _inner.DeadLetterAsync(message, reason, cancellationToken);
}
