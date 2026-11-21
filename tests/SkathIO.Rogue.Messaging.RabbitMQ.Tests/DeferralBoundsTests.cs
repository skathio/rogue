using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// Duplicates of one message id cannot stall the consumer: of the deliveries the inbox turns away as
/// busy, at most half the transport's unsettled window waits out a delay, and with no retry delay a busy
/// delivery still does not come straight back. A busy delivery is deferred, so it never costs an
/// attempt. Built in memory over a transport with no native delay of either kind; RabbitMQ delays its
/// deferrals on the broker, so there no busy delivery waits in the consumer (see BrokerDelayQueueTests).
/// </summary>
public sealed class BusyInboxBoundTests
{
    private static readonly TimeSpan _oneMinute = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData(4, 0, 0)]
    [InlineData(4, 1, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(4, 3, 2)]
    [InlineData(6, 3, 3)]
    [InlineData(6, 4, 3)]
    [InlineData(1, 1, 0)]
    public async Task AtMostHalfTheWindow_OfBusyDuplicates_WaitsOutADelay(int window, int duplicates, int waits)
    {
        await using BusyHost host = BusyHost.Create(window, _oneMinute);
        await host.SendDuplicatesAsync(duplicates);

        Task<int> run = host.RunOnceAsync();

        // Each duplicate either starts a wait or is settled; a wait ends only when the test lets it.
        await host.Events.WaitForAsync(duplicates);
        Assert.Equal(waits, host.Clock.TimerCount);
        Assert.Equal(duplicates, await host.FinishAsync(run));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(4, 2)]
    [InlineData(4, 3)]
    [InlineData(6, 4)]
    public async Task ABusyDuplicate_IsPutBack_WithoutCostingAnAttempt(int window, int duplicates)
    {
        await using BusyHost host = BusyHost.Create(window, _oneMinute);
        await host.SendDuplicatesAsync(duplicates);

        Task<int> run = host.RunOnceAsync();
        await host.Events.WaitForAsync(duplicates);
        await host.FinishAsync(run);

        IReadOnlyList<ReceivedMessage> back = await host.ReceiveLaterAsync(duplicates);
        Assert.Equal(duplicates, back.Count);
        Assert.All(back, delivery => Assert.Equal(1, delivery.DeliveryCount));
        Assert.Empty(host.Transport.Inner.DeadLettered);
    }

    [Fact]
    public async Task WithNoRetryDelay_ABusyDuplicate_DoesNotComeStraightBack()
    {
        await using BusyHost host = BusyHost.Create(4, TimeSpan.Zero);
        await host.SendDuplicatesAsync(1);

        Task<int> run = host.RunOnceAsync();
        await host.Events.WaitForAsync(1);

        // Coming straight back is what lets duplicates loop at broker speed.
        Assert.Empty(await host.Transport.ReceiveAsync(1, CancellationToken.None));

        Assert.Equal(1, await host.FinishAsync(run));
        ReceivedMessage back = Assert.Single(await host.ReceiveLaterAsync(1));
        Assert.Equal(1, back.DeliveryCount);
    }

    private sealed class AlwaysBusyInbox : IMessageInbox
    {
        public ValueTask<InboxClaimResult> TryClaimAsync(string messageId, CancellationToken cancellationToken)
            => new ValueTask<InboxClaimResult>(new InboxClaimResult(InboxClaim.Busy, 0));

        public ValueTask CompleteAsync(string messageId, long claimToken, CancellationToken cancellationToken) => default;

        public ValueTask ReleaseAsync(string messageId, long claimToken, CancellationToken cancellationToken) => default;
    }

    /// <summary>A consumer whose inbox always says busy, over a windowed transport with no native delay.</summary>
    private sealed class BusyHost : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private BusyHost(ServiceProvider services, ManualClock clock, NoDelayTransport transport, EventCount events)
        {
            _services = services;
            Clock = clock;
            Transport = transport;
            Events = events;
        }

        public ManualClock Clock { get; }

        public NoDelayTransport Transport { get; }

        /// <summary>Counts each timer started and each delivery settled.</summary>
        public EventCount Events { get; }

        public static BusyHost Create(int window, TimeSpan retryDelay)
        {
            var events = new EventCount();
            var clock = new ManualClock(events);
            var transport = new NoDelayTransport(new InMemoryTransport(clock), window, events);
            var collection = new ServiceCollection();
            collection.AddSingleton<TimeProvider>(clock);
            collection.AddSingleton<MessageProbe>();
            collection.AddSingleton<IMessageTransport>(transport);
            collection.AddSingleton<IMessageInbox>(new AlwaysBusyInbox());
            collection.AddRogue();
            collection.AddRogueMessaging(options =>
            {
                options.SerializerContext = TestMessagesJson.Default;
                options.RetryDelay = retryDelay;
                options.MaxRetryDelay = _oneMinute;
                options.MaxAttempts = 3;
                options.MaxConcurrency = 8;
            });
            return new BusyHost(collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), clock, transport, events);
        }

        /// <summary>Sends <paramref name="count"/> copies of one message, all with the same id.</summary>
        public ValueTask SendDuplicatesAsync(int count)
        {
            var envelopes = new MessageEnvelope[count];
            for (int i = 0; i < count; i++)
            {
                envelopes[i] = new MessageEnvelope
                {
                    Id = "dup-1",
                    Name = "rmq.order-placed",
                    Body = Encoding.UTF8.GetBytes("{\"Id\":1,\"Note\":\"ok\"}"),
                };
            }
            return Transport.SendAsync(envelopes, CancellationToken.None);
        }

        public Task<int> RunOnceAsync() => _services.GetRequiredService<MessageConsumer>().RunOnceAsync();

        /// <summary>Lets every wait end, now and from here on, and returns what the run received.</summary>
        public async Task<int> FinishAsync(Task<int> run)
        {
            Clock.FireFromNowOn();
            return await run;
        }

        /// <summary>Moves the clock far ahead and receives what was put back, up to one more than expected.</summary>
        public async Task<IReadOnlyList<ReceivedMessage>> ReceiveLaterAsync(int expected)
        {
            Clock.Advance(TimeSpan.FromDays(365));
            return await Transport.ReceiveAsync(expected + 1, CancellationToken.None);
        }

        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }

    /// <summary>Completes a wait once a count of events is reached, so a test waits on what happened, not on time.</summary>
    private sealed class EventCount
    {
        private readonly object _gate = new object();
        private readonly List<(int Target, TaskCompletionSource Done)> _waits = new List<(int Target, TaskCompletionSource Done)>();
        private int _count;

        public void Signal()
        {
            lock (_gate)
            {
                _count++;
                foreach ((int target, TaskCompletionSource done) in _waits)
                {
                    if (target <= _count)
                    {
                        done.TrySetResult();
                    }
                }
            }
        }

        public Task WaitForAsync(int target)
        {
            lock (_gate)
            {
                if (_count >= target)
                {
                    return Task.CompletedTask;
                }
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waits.Add((target, done));
                return done.Task;
            }
        }
    }

    /// <summary>
    /// An in-memory transport with no native delay: a message nacked or deferred is put back at once,
    /// whatever delay it is given. It reports an unsettled window and counts each settle.
    /// </summary>
    private sealed class NoDelayTransport : IMessageTransport
    {
        private readonly EventCount _events;

        public NoDelayTransport(InMemoryTransport inner, int window, EventCount events)
        {
            Inner = inner;
            UnsettledLimit = window;
            _events = events;
        }

        public InMemoryTransport Inner { get; }

        public int? UnsettledLimit { get; }

        public TransportCapabilities Capabilities => TransportCapabilities.NativeDeadLetter;

        public ValueTask SendAsync(IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken) => Inner.SendAsync(messages, cancellationToken);

        public ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxCount, CancellationToken cancellationToken) => Inner.ReceiveAsync(maxCount, cancellationToken);

        public ValueTask AckAsync(ReceivedMessage message, CancellationToken cancellationToken) => SignalAfter(Inner.AckAsync(message, cancellationToken));

        public ValueTask NackAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken)
            => SignalAfter(Inner.NackAsync(message, TimeSpan.Zero, cancellationToken));

        public ValueTask DeferAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken)
            => SignalAfter(Inner.DeferAsync(message, TimeSpan.Zero, cancellationToken));

        public ValueTask DeadLetterAsync(ReceivedMessage message, string reason, CancellationToken cancellationToken)
            => SignalAfter(Inner.DeadLetterAsync(message, reason, cancellationToken));

        private async ValueTask SignalAfter(ValueTask settle)
        {
            await settle;
            _events.Signal();
        }
    }

    /// <summary>
    /// A clock that moves only when a test moves it. Each timer made from it waits until the test lets
    /// timers fire; from then on, new timers fire at once.
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new object();
        private readonly List<(TimerCallback Callback, object? State)> _pending = new List<(TimerCallback Callback, object? State)>();
        private readonly EventCount _events;
        private DateTimeOffset _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private bool _fireAtOnce;
        private int _timers;

        public ManualClock(EventCount events)
        {
            _events = events;
        }

        public int TimerCount => Volatile.Read(ref _timers);

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

        public void FireFromNowOn()
        {
            (TimerCallback Callback, object? State)[] due;
            lock (_gate)
            {
                _fireAtOnce = true;
                due = _pending.ToArray();
                _pending.Clear();
            }
            foreach ((TimerCallback callback, object? state) in due)
            {
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            bool fireNow;
            lock (_gate)
            {
                fireNow = _fireAtOnce;
                if (!fireNow)
                {
                    _pending.Add((callback, state));
                }
            }
            Interlocked.Increment(ref _timers);
            _events.Signal();
            if (fireNow)
            {
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
            return new HeldTimer();
        }

        private sealed class HeldTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => default;
        }
    }
}

/// <summary>
/// A deferral never costs an attempt, even when the service's queue is full: the deferred copy waits in
/// the wait queue, which keeps it until the service's queue takes it back. Against a real broker in a
/// container.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class FailedPutBackTests
{
    // Only a bound on how long a broken test waits; no assertion depends on time.
    private static readonly TimeSpan _giveUpAfter = TimeSpan.FromMinutes(2);

    // A deferral with no delay skips the wait queue, so these tests defer for the shortest delay that uses it.
    private static readonly TimeSpan _shortestWait = TimeSpan.FromTicks(1);

    private readonly RabbitMqFixture _broker;

    public FailedPutBackTests(RabbitMqFixture broker) => _broker = broker;

    [RabbitMqFact]
    public async Task ADeferredDelivery_WhileTheServiceQueueIsFull_ComesBackWithItsAttemptsKept()
    {
        string run = Guid.NewGuid().ToString("N");
        var options = new RabbitMqTransportOptions
        {
            ConnectionUri = _broker.ConnectionUri,
            Exchange = "test-" + run,
            ServiceName = "service-" + run,
            DeliveryStampKey = RabbitMqHost.DeliveryStampKey,
            PrefetchCount = 1,
        };
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await using var transport = new RabbitMqTransport(options, new[] { "rmq.order-placed" });
            await LimitQueueToOneAsync(transport, raw, options);
            await transport.SendAsync(new[] { Envelope("deferred") }, CancellationToken.None);
            ReceivedMessage first = await ReceiveAsync(transport, "deferred");
            await FillQueueAsync(transport);

            await DeferQuietlyAsync(transport, first, _shortestWait);

            ReceivedMessage again = await ReceiveAsync(transport, "deferred");
            Assert.Equal(first.DeliveryCount, again.DeliveryCount);
            Assert.Equal(1, again.DeliveryCount);
            await transport.AckAsync(again, CancellationToken.None);
        }
    }

    [RabbitMqFact]
    public async Task ADeferredDelivery_WhoseWaitQueueCopyIsRefused_IsDeadLetteredWithItsCountAndAReason()
    {
        string run = Guid.NewGuid().ToString("N");
        var options = new RabbitMqTransportOptions
        {
            ConnectionUri = _broker.ConnectionUri,
            Exchange = "test-" + run,
            ServiceName = "service-" + run,
            DeliveryStampKey = RabbitMqHost.DeliveryStampKey,
            PrefetchCount = 1,
        };
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await using var transport = new RabbitMqTransport(options, new[] { "rmq.order-placed" });
            await transport.SendAsync(new[] { Envelope("connect", "rmq.report") }, CancellationToken.None);
            await FillWaitQueueAsync(raw, options);
            await transport.SendAsync(new[] { Envelope("refused") }, CancellationToken.None);
            await transport.NackAsync(await ReceiveAsync(transport, "refused"), TimeSpan.Zero, CancellationToken.None);
            ReceivedMessage second = await ReceiveAsync(transport, "refused");
            Assert.Equal(2, second.DeliveryCount);

            await DeferQuietlyAsync(transport, second, _shortestWait);

            BasicGetResult kept = await GetAsync(raw, options.DeadLetterQueue!);
            Assert.Equal("refused", kept.BasicProperties.MessageId);
            Assert.Equal(2, Convert.ToInt32(kept.BasicProperties.Headers![RabbitMqWire.DeliveryCountHeader], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Contains("wait queue refused", RabbitMqWire.HeaderString(kept.BasicProperties, RabbitMqWire.DeadLetterReasonHeader), StringComparison.Ordinal);
        }
    }

    [RabbitMqFact]
    public async Task ADeferralWithNoDelay_WhoseServiceQueueCopyIsRefused_IsDeadLetteredWithItsCountAndAReason()
    {
        string run = Guid.NewGuid().ToString("N");
        var options = new RabbitMqTransportOptions
        {
            ConnectionUri = _broker.ConnectionUri,
            Exchange = "test-" + run,
            ServiceName = "service-" + run,
            DeliveryStampKey = RabbitMqHost.DeliveryStampKey,
            PrefetchCount = 1,
        };
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await using var transport = new RabbitMqTransport(options, new[] { "rmq.order-placed" });
            await LimitQueueToOneAsync(transport, raw, options);
            await transport.SendAsync(new[] { Envelope("released") }, CancellationToken.None);
            ReceivedMessage first = await ReceiveAsync(transport, "released");
            await FillQueueAsync(transport);

            await DeferQuietlyAsync(transport, first, TimeSpan.Zero);

            BasicGetResult kept = await GetAsync(raw, options.DeadLetterQueue!);
            Assert.Equal("released", kept.BasicProperties.MessageId);
            Assert.Equal(1, Convert.ToInt32(kept.BasicProperties.Headers![RabbitMqWire.DeliveryCountHeader], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Contains("service's queue refused", RabbitMqWire.HeaderString(kept.BasicProperties, RabbitMqWire.DeadLetterReasonHeader), StringComparison.Ordinal);
            Assert.Equal(0u, (await raw.QueueDeclarePassiveAsync(options.WaitQueue!)).MessageCount);
        }
    }

    private static MessageEnvelope Envelope(string id, string name = "rmq.order-placed") => new MessageEnvelope
    {
        Id = id,
        Name = name,
        Body = Encoding.UTF8.GetBytes("{\"Id\":1,\"Note\":\"ok\"}"),
    };

    // The wait queue is made again to hold one message, refusing any publish past that, and filled.
    private static async Task FillWaitQueueAsync(IChannel raw, RabbitMqTransportOptions options)
    {
        string wait = options.WaitQueue!;
        await raw.QueueDeleteAsync(wait);
        await raw.QueueDeclareAsync(wait, true, false, false, new Dictionary<string, object?>
        {
            ["x-max-length"] = 1,
            ["x-overflow"] = "reject-publish",
        });
        await raw.BasicPublishAsync(string.Empty, wait, Encoding.UTF8.GetBytes("filler"));
    }

    private static async Task<BasicGetResult> GetAsync(IChannel raw, string queue)
    {
        DateTime giveUp = DateTime.UtcNow + _giveUpAfter;
        while (true)
        {
            Assert.True(DateTime.UtcNow < giveUp, "Gave up waiting for a message on " + queue + ".");
            if (await raw.BasicGetAsync(queue, true) is { } taken)
            {
                return taken;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }

    // Once the transport has declared its topology, its queue is made again to hold one message and
    // refuse any publish past that. The broker refuses a redeclare with other arguments, so this is the
    // one way to give the transport's queue a limit.
    private static async Task LimitQueueToOneAsync(RabbitMqTransport transport, IChannel raw, RabbitMqTransportOptions options)
    {
        // The service is not bound to this name, so the message goes nowhere; sending it connects.
        await transport.SendAsync(new[] { Envelope("connect", "rmq.report") }, CancellationToken.None);
        string queue = options.ServiceName!;
        await raw.QueueDeleteAsync(queue);
        await raw.QueueDeclareAsync(queue, true, false, false, new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-dead-letter-exchange"] = options.DeadLetterExchange,
            ["x-dead-letter-routing-key"] = queue,
            ["x-max-length"] = 1,
            ["x-overflow"] = "reject-publish",
        });
        await raw.QueueBindAsync(queue, options.Exchange, "rmq.order-placed");
    }

    // Sends more messages until the broker refuses one, so the queue is over its limit and refuses the
    // copy the wait queue moves back too, until room is made. How far past the limit a quorum queue lets
    // it go is the broker's choice.
    private static async Task FillQueueAsync(RabbitMqTransport transport)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                await transport.SendAsync(new[] { Envelope("filler-" + i) }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                return;
            }
        }
        Assert.Fail("The queue never refused a message, so the put-back copy would not be refused either.");
    }

    // Any failure of the deferral is reported as the transport chooses; only where it goes is checked.
    private static async Task DeferQuietlyAsync(RabbitMqTransport transport, ReceivedMessage delivery, TimeSpan delay)
    {
        try
        {
            await transport.DeferAsync(delivery, delay, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // Reported as a failed settle.
        }
    }

    // Receives until the message with this id arrives, acking any other on the way.
    private static async Task<ReceivedMessage> ReceiveAsync(RabbitMqTransport transport, string id)
    {
        DateTime giveUp = DateTime.UtcNow + _giveUpAfter;
        while (true)
        {
            Assert.True(DateTime.UtcNow < giveUp, "Gave up waiting for message " + id + ".");
            foreach (ReceivedMessage delivery in await transport.ReceiveAsync(1, CancellationToken.None))
            {
                if (delivery.Envelope.Id == id)
                {
                    return delivery;
                }
                await transport.AckAsync(delivery, CancellationToken.None);
            }
        }
    }
}
