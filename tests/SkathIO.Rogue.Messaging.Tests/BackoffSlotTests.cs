using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>
/// On a transport without native delay, a failed message waits out its retry delay without holding one
/// of the consumer's <see cref="MessagingOptions.MaxConcurrency"/> slots, so a sender of failing
/// messages cannot stall every other message. The delay itself still holds: the message comes back only
/// once it has passed, and a consumer that stops settles every message still waiting.
/// </summary>
public sealed class BackoffSlotTests
{
    private static readonly TimeSpan Backoff = TimeSpan.FromHours(1);

    // Waits only so a broken consumer fails the test instead of hanging it; nothing is timed.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 1)]
    [InlineData(3, 3)]
    [InlineData(3, 4)]
    public async Task FailingMessagesInBackoff_DoNotStallOtherMessages(int maxConcurrency, int failing)
    {
        await using BackoffHost host = BackoffHost.Create(maxConcurrency);
        using var stopping = new CancellationTokenSource();
        Task running = host.Consumer.RunAsync(stopping.Token);

        await host.SendAsync(Enumerable.Range(1, failing).Select(static i => Order(i, "fail")));
        await host.WaitForRunsAsync(failing, "every failing message ran once");
        await host.SendAsync(Enumerable.Range(101, 2).Select(static i => Order(i, "n")));
        await host.WaitForRunsAsync(failing + 2, "the other messages ran while the failing ones were in backoff");

        // Released from its slot, a failed message is still not delivered again before its delay.
        Assert.Equal(failing, host.Probe.Ran.Count(static r => int.Parse(r.AsSpan("placed:".Length)) <= 100));
        Assert.Empty(host.DeadLettered);

        await stopping.CancelAsync();
        await Guard(running, "the consumer stopped");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    public async Task FailingMessagesInBackoff_AreDeliveredAgainOnceTheirDelayHasPassed(int maxConcurrency, int failing)
    {
        await using BackoffHost host = BackoffHost.Create(maxConcurrency);
        using var stopping = new CancellationTokenSource();
        Task running = host.Consumer.RunAsync(stopping.Token);

        await host.SendAsync(Enumerable.Range(1, failing).Select(static i => Order(i, "fail")));
        await host.WaitForRunsAsync(failing, "every failing message ran once");
        await host.SendAsync(new[] { Order(101, "n") });
        await host.WaitForRunsAsync(failing + 1, "the other message ran while the failing ones were in backoff");

        host.Clock.Advance(Backoff);
        await host.WaitForRunsAsync((2 * failing) + 1, "every failing message ran again after its delay");
        await stopping.CancelAsync();
        await Guard(running, "the consumer stopped");

        // The second delivery is the last of two attempts, so each failing message is dead-lettered.
        Assert.Equal(Enumerable.Range(1, failing).Select(static i => 2), Enumerable.Range(1, failing).Select(i => host.Probe.Ran.Count(r => r == "placed:" + i)));
        Assert.Equal(failing, host.DeadLettered.Count);
        Assert.Empty(await host.DrainAsync());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    public async Task Stopping_SettlesEveryMessageStillInBackoff_AndLosesNone(int maxConcurrency, int failing)
    {
        await using BackoffHost host = BackoffHost.Create(maxConcurrency);
        using var stopping = new CancellationTokenSource();
        Task running = host.Consumer.RunAsync(stopping.Token);

        await host.SendAsync(Enumerable.Range(1, failing).Select(static i => Order(i, "fail")));
        await host.WaitForRunsAsync(failing, "every failing message ran once");
        await host.SendAsync(new[] { Order(101, "n") });
        await host.WaitForRunsAsync(failing + 1, "the other message ran while the failing ones were in backoff");

        await stopping.CancelAsync();
        await Guard(running, "the consumer stopped and settled the messages in backoff");

        MessageEnvelope[] back = await host.DrainAsync();
        Assert.Equal(Enumerable.Range(1, failing).Select(static i => "{\"Id\":" + i + ",\"Note\":\"fail\"}"), back.Select(static e => System.Text.Encoding.UTF8.GetString(e.Body.Span)).OrderBy(static b => b, StringComparer.Ordinal));
        Assert.Empty(host.DeadLettered);
    }

    private static MessageEnvelope Order(int id, string note) =>
        MessagingHost.Envelope("test.order-placed", "{\"Id\":" + id + ",\"Note\":\"" + note + "\"}");

    private static async Task Guard(Task task, string what)
    {
        try
        {
            await task.WaitAsync(HangGuard);
        }
        catch (TimeoutException)
        {
            Assert.Fail("Not done: " + what + ".");
        }
    }

    /// <summary>
    /// A consumer on a transport that, like RabbitMQ, has no native delay: a nacked message is ready
    /// again at once, whatever delay it was nacked with.
    /// </summary>
    private sealed class BackoffHost : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly InMemoryTransport _inner;

        private BackoffHost(ServiceProvider services, InMemoryTransport inner, HoldingClock clock)
        {
            _services = services;
            _inner = inner;
            Clock = clock;
        }

        public HoldingClock Clock { get; }

        public MessageConsumer Consumer => _services.GetRequiredService<MessageConsumer>();

        public MessageProbe Probe => _services.GetRequiredService<MessageProbe>();

        public IReadOnlyList<DeadLetteredMessage> DeadLettered => _inner.DeadLettered;

        public static BackoffHost Create(int maxConcurrency)
        {
            var clock = new HoldingClock(Backoff);
            var inner = new InMemoryTransport(clock);
            var collection = new ServiceCollection();
            collection.AddSingleton<TimeProvider>(clock);
            collection.AddSingleton<MessageProbe>();
            collection.AddSingleton<IMessageTransport>(new NoDelayTransport(inner));
            collection.AddRogue();
            collection.AddRogueMessaging(options =>
            {
                options.SerializerContext = TestMessagesJson.Default;
                options.MaxConcurrency = maxConcurrency;
                options.BatchSize = maxConcurrency;
                options.MaxAttempts = 2;
                options.RetryDelay = Backoff;
                options.MaxRetryDelay = Backoff;
                options.PollInterval = TimeSpan.FromMilliseconds(5);
            });
            return new BackoffHost(collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), inner, clock);
        }

        public ValueTask SendAsync(IEnumerable<MessageEnvelope> envelopes) => _inner.SendAsync(envelopes.ToArray(), CancellationToken.None);

        public async Task<MessageEnvelope[]> DrainAsync() =>
            (await _inner.ReceiveAsync(MessagingOptions.MaxBatchSize, CancellationToken.None)).Select(static m => m.Envelope).ToArray();

        public async Task WaitForRunsAsync(int count, string what)
        {
            using var guard = new CancellationTokenSource(HangGuard);
            while (Probe.Ran.Count < count)
            {
                if (guard.IsCancellationRequested)
                {
                    Assert.Fail("Not done: " + what + " (" + Probe.Ran.Count + " of " + count + " handler runs).");
                }
                await Task.Delay(5, CancellationToken.None);
            }
        }

        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }

    /// <summary>An <see cref="InMemoryTransport"/> without native delay: it puts every message back with none.</summary>
    private sealed class NoDelayTransport : IMessageTransport
    {
        private readonly InMemoryTransport _inner;

        public NoDelayTransport(InMemoryTransport inner) => _inner = inner;

        public TransportCapabilities Capabilities => TransportCapabilities.NativeDeadLetter;

        public ValueTask SendAsync(IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken) => _inner.SendAsync(messages, cancellationToken);

        public ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxCount, CancellationToken cancellationToken) => _inner.ReceiveAsync(maxCount, cancellationToken);

        public ValueTask AckAsync(ReceivedMessage message, CancellationToken cancellationToken) => _inner.AckAsync(message, cancellationToken);

        public ValueTask NackAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken) => _inner.NackAsync(message, TimeSpan.Zero, cancellationToken);

        public ValueTask DeferAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken) => _inner.DeferAsync(message, TimeSpan.Zero, cancellationToken);

        public ValueTask DeadLetterAsync(ReceivedMessage message, string reason, CancellationToken cancellationToken) => _inner.DeadLetterAsync(message, reason, cancellationToken);
    }

    /// <summary>
    /// Real time for short waits, such as the consumer's poll interval. A wait of at least the hold
    /// length, such as a retry delay, ends only once the test moves the clock past it, so no backoff
    /// ends while a test looks at what happens during it.
    /// </summary>
    private sealed class HoldingClock : TimeProvider
    {
        private readonly object _gate = new object();
        private readonly TimeSpan _holdFrom;
        private readonly List<HeldTimer> _held = new List<HeldTimer>();
        private DateTimeOffset _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public HoldingClock(TimeSpan holdFrom) => _holdFrom = holdFrom;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public void Advance(TimeSpan by)
        {
            List<HeldTimer> due;
            lock (_gate)
            {
                _now += by;
                due = _held.Where(t => t.Due <= _now).ToList();
                _held.RemoveAll(t => t.Due <= _now);
            }
            foreach (HeldTimer timer in due)
            {
                ThreadPool.QueueUserWorkItem(_ => timer.Fire());
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < _holdFrom)
            {
                return System.CreateTimer(callback, state, dueTime, period);
            }
            var timer = new HeldTimer(this, () => callback(state));
            timer.Change(dueTime, period);
            return timer;
        }

        private void Hold(HeldTimer timer, TimeSpan dueTime)
        {
            lock (_gate)
            {
                _held.Remove(timer);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    timer.Due = _now + dueTime;
                    _held.Add(timer);
                }
            }
        }

        private void Drop(HeldTimer timer)
        {
            lock (_gate)
            {
                _held.Remove(timer);
            }
        }

        private sealed class HeldTimer : ITimer
        {
            private readonly HoldingClock _clock;
            private readonly Action _fire;

            public HeldTimer(HoldingClock clock, Action fire)
            {
                _clock = clock;
                _fire = fire;
            }

            public DateTimeOffset Due { get; set; }

            public void Fire() => _fire();

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _clock.Hold(this, dueTime);
                return true;
            }

            public void Dispose() => _clock.Drop(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }
        }
    }
}
