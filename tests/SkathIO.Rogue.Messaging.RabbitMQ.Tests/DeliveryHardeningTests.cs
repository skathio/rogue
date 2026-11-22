using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// Defences around a delivery: a stamp key that starts or ends with white space is refused, failed
/// deliveries waiting out a retry delay never take more than half the transport's unsettled window,
/// the type property names a default-exchange delivery only when it is a name the service receives,
/// and a redelivered delivery counts the broker's own redeliveries. Deliveries are built in memory,
/// so no broker is needed.
/// </summary>
public sealed class DeliveryHardeningTests
{
    private const string Failing = "{\"Id\":1,\"Note\":\"fail\"}";
    private const string Key = RabbitMqHost.DeliveryStampKey;

    public static TheoryData<string> PaddedKeys => new TheoryData<string>
    {
        Key + "\n",
        " " + Key,
        Key + " ",
        "\t" + Key,
        Key + "\r\n",
        " " + Key,
    };

    [Theory]
    [MemberData(nameof(PaddedKeys))]
    public void AStampKey_StartingOrEndingWithWhiteSpace_IsRefused_WithoutShowingTheKey(string key)
    {
        RabbitMqTransportOptions options = Options(key);

        ArgumentException refused = Assert.Throws<ArgumentException>(options.Validate);

        Assert.Equal(nameof(RabbitMqTransportOptions.DeliveryStampKey), refused.ParamName);
        Assert.DoesNotContain(Key, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, refused.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AStampKey_WithWhiteSpaceOnlyInside_IsAccepted()
    {
        Options("test-delivery-stamp key-32-bytes").Validate();
    }

    [Fact]
    public void ThePaddedKeyRule_AppliesAtRegistration()
    {
        var services = new ServiceCollection();

        ArgumentException refused = Assert.Throws<ArgumentException>(() => services.AddRogueRabbitMqTransport(o => o.DeliveryStampKey = Key + "\n"));

        Assert.Equal(nameof(RabbitMqTransportOptions.DeliveryStampKey), refused.ParamName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(ushort.MaxValue)]
    public async Task TheRabbitMqTransport_ReportsItsPrefetchCount_AsItsUnsettledWindow(int prefetch)
    {
        RabbitMqTransportOptions options = Options(Key);
        options.PrefetchCount = prefetch;
        await using var transport = new RabbitMqTransport(options, Array.Empty<string>());

        Assert.Equal(prefetch, ((IMessageTransport)transport).UnsettledLimit);
    }

    [Fact]
    public void ATransportThatSetsNoWindow_ReportsNone()
    {
        Assert.Null(((IMessageTransport)new InMemoryTransport()).UnsettledLimit);
    }

    [Fact]
    public async Task WithAWindowOfOne_AFailedDelivery_IsNackedAtOnce_WithoutWaiting_AndItsCountRaised()
    {
        await using ConsumerHost host = ConsumerHost.Create(window: 1, maxAttempts: 5, fireAtOnce: true);

        await host.SendAsync(Envelope("m-1"));
        await host.RunOnceAsync();

        Assert.Empty(host.Clock.Timers);
        Assert.Equal(TimeSpan.Zero, Assert.Single(host.Transport.NackDelays));
        Assert.Equal(2, Assert.Single(await host.Transport.ReceiveAsync(1, CancellationToken.None)).DeliveryCount);
    }

    [Fact]
    public async Task WithAWindowOfOne_AMessageThatAlwaysFails_IsStillDeadLetteredAfterExactlyMaxAttempts()
    {
        await using ConsumerHost host = ConsumerHost.Create(window: 1, maxAttempts: 3, fireAtOnce: true);

        await host.SendAsync(Envelope("m-1"));
        for (int run = 0; run < 3; run++)
        {
            Assert.Equal(1, await host.RunOnceAsync());
        }

        Assert.Equal(3, host.Probe.Count("placed:1"));
        Assert.Equal(2, host.Transport.NackDelays.Count);
        Assert.Single(host.Transport.Inner.DeadLettered);
        Assert.Empty(host.Clock.Timers);
        Assert.Equal(0, await host.RunOnceAsync());
    }

    [Fact]
    public async Task AtMostHalfTheWindow_WaitsOutARetryDelay_AndOneMore_IsNackedAtOnce()
    {
        await using ConsumerHost host = ConsumerHost.Create(window: 4, maxAttempts: 5);
        await host.SendAsync(Envelope("m-1"), Envelope("m-2"), Envelope("m-3"));

        Task<int> run = host.RunOnceAsync();

        // Each failed delivery either starts a wait or is nacked; neither ends until the test lets it.
        await host.Settled.WaitForAsync(3);
        Assert.Equal(2, host.Clock.Timers.Count);
        Assert.Equal(TimeSpan.Zero, Assert.Single(host.Transport.NackDelays));

        host.Clock.FireAll();
        Assert.Equal(3, await run);

        Assert.Equal(3, host.Transport.NackDelays.Count);
        IReadOnlyList<ReceivedMessage> again = await host.Transport.ReceiveAsync(3, CancellationToken.None);
        Assert.Equal(3, again.Count);
        Assert.All(again, delivery => Assert.Equal(2, delivery.DeliveryCount));
    }

    [Fact]
    public async Task AtMostHalfTheWindow_WaitsOutARetryDelay_ForDeliveriesTheInboxTurnsAwayAsBusy()
    {
        await using ConsumerHost host = ConsumerHost.Create(window: 4, maxAttempts: 5, inbox: new AlwaysBusyInbox());
        await host.SendAsync(Envelope("m-1"), Envelope("m-2"), Envelope("m-3"));

        Task<int> run = host.RunOnceAsync();

        // Three duplicates of a busy id: two may wait out the delay, the third is put back at once.
        await host.Settled.WaitForAsync(3);
        Assert.Equal(2, host.Clock.Timers.Count);

        host.Clock.FireAll();
        Assert.Equal(3, await run);
    }

    private sealed class AlwaysBusyInbox : IMessageInbox
    {
        public ValueTask<InboxClaimResult> TryClaimAsync(string messageId, CancellationToken cancellationToken)
            => new ValueTask<InboxClaimResult>(new InboxClaimResult(InboxClaim.Busy, 0));

        public ValueTask CompleteAsync(string messageId, long claimToken, CancellationToken cancellationToken) => default;

        public ValueTask ReleaseAsync(string messageId, long claimToken, CancellationToken cancellationToken) => default;
    }

    [Fact]
    public async Task AWaitThatEnds_GivesItsPlaceBack_ToTheNextFailedDelivery()
    {
        await using ConsumerHost host = ConsumerHost.Create(window: 2, maxAttempts: 5);

        await host.SendAsync(Envelope("m-1"));
        Task<int> first = host.RunOnceAsync();
        await host.Settled.WaitForAsync(1);
        host.Clock.FireAll();
        await first;
        ReceivedMessage retried = Assert.Single(await host.Transport.ReceiveAsync(1, CancellationToken.None));
        await host.Transport.AckAsync(retried, CancellationToken.None);

        // The first delivery's wait and its nack are two events; the second delivery adds a third.
        await host.SendAsync(Envelope("m-2"));
        Task<int> second = host.RunOnceAsync();
        await host.Settled.WaitForAsync(3);
        host.Clock.FireAll();
        await second;

        Assert.Equal(2, host.Clock.Timers.Count);
    }

    [Theory]
    [InlineData("orders.placed", "orders.placed")]
    [InlineData("stock.reserved", "stock.reserved")]
    [InlineData("payments.refunded", "payments.refunded")]
    [InlineData("orders.shipped", "")]
    [InlineData("rogue.anything", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void OnTheDefaultExchange_TheTypeProperty_NamesADelivery_OnlyWhenTheServiceReceivesThatName(string? type, string expected)
    {
        RabbitMqTransportOptions options = Options(Key)
            .MapHeader("event-type", "StockReserved", "stock.reserved")
            .MapRoutingKey("legacy", "legacy.refund", "payments.refunded");
        var known = new HashSet<string>(StringComparer.Ordinal) { "orders.placed", "stock.reserved", "payments.refunded" };
        var delivery = new BasicDeliverEventArgs(
            "consumer", 1, false, string.Empty, "billing", new BasicProperties { Type = type }, Encoding.UTF8.GetBytes("{}"));

        Assert.Equal(expected, RabbitMqWire.ToEnvelope(options, delivery, known).Name);
    }

    [Fact]
    public void AHeaderMapOnTheDefaultExchange_StillNamesTheDelivery()
    {
        RabbitMqTransportOptions options = Options(Key).MapHeader("event-type", "StockReserved", "stock.reserved");
        var properties = new BasicProperties
        {
            Type = "orders.shipped",
            Headers = new Dictionary<string, object?> { ["event-type"] = Encoding.UTF8.GetBytes("StockReserved") },
        };
        var delivery = new BasicDeliverEventArgs("consumer", 1, false, string.Empty, "billing", properties, Encoding.UTF8.GetBytes("{}"));

        Assert.Equal("stock.reserved", RabbitMqWire.ToEnvelope(options, delivery, RabbitMqWire.KnownNames(options, Array.Empty<string>())).Name);
    }

    [Fact]
    public void OnTheOwnExchange_TheRoutingKeyStillNamesTheDelivery_WhateverTheKnownNames()
    {
        var delivery = new BasicDeliverEventArgs(
            "consumer", 1, false, "shop", "orders.placed", new BasicProperties { Type = "orders.shipped" }, Encoding.UTF8.GetBytes("{}"));

        Assert.Equal("orders.placed", RabbitMqWire.ToEnvelope(Options(Key), delivery, new HashSet<string>(StringComparer.Ordinal)).Name);
    }

    [Fact]
    public async Task TheTransport_KnowsItsSubscriptions_AndEveryMappedName()
    {
        RabbitMqTransportOptions options = Options(Key)
            .MapHeader("event-type", "StockReserved", "stock.reserved")
            .MapRoutingKey("legacy", "legacy.refund", "payments.refunded");
        await using var transport = new RabbitMqTransport(options, new[] { "orders.placed", "orders.placed" });

        Assert.Equal(new[] { "orders.placed", "payments.refunded", "stock.reserved" }, transport.KnownNames.OrderBy(static n => n, StringComparer.Ordinal));
    }

    public static TheoryData<object?, int> BrokerCounts => new TheoryData<object?, int>
    {
        { 4, 5 },
        { 4L, 5 },
        { (short)4, 5 },
        { (byte)4, 5 },
        { 1, 2 },
        { 0, 2 },
        { -7, 2 },
        { long.MinValue, 2 },
        { RabbitMqWire.MaxDeliveryCount, 1 + RabbitMqWire.MaxDeliveryCount },
        { (long)RabbitMqWire.MaxDeliveryCount + 1, 1 + RabbitMqWire.MaxDeliveryCount },
        { long.MaxValue, 1 + RabbitMqWire.MaxDeliveryCount },
        { Encoding.UTF8.GetBytes("4"), 2 },
        { "4", 2 },
        { 4.0, 2 },
        { null, 2 },
    };

    [Theory]
    [MemberData(nameof(BrokerCounts))]
    public void AnUnstampedRedelivery_AddsTheBrokersDeliveryCount_HeldToItsRange_Or1WhenItIsNotAnInteger(object? header, int expected)
    {
        var stamp = new DeliveryStamp(Key);
        BasicDeliverEventArgs delivery = Redelivery(true, ("x-delivery-count", header));

        Assert.Equal(expected, stamp.CountOf(delivery, RabbitMqWire.ToEnvelope(Options(Key), delivery)));
    }

    [Fact]
    public void AnUnstampedRedelivery_WithoutTheBrokersCount_Counts2()
    {
        var stamp = new DeliveryStamp(Key);
        BasicDeliverEventArgs delivery = Redelivery(true);

        Assert.Equal(2, stamp.CountOf(delivery, RabbitMqWire.ToEnvelope(Options(Key), delivery)));
    }

    [Fact]
    public void TheBrokersCount_IsIgnored_OnADeliveryNotMarkedRedelivered()
    {
        var stamp = new DeliveryStamp(Key);
        BasicDeliverEventArgs delivery = Redelivery(false, ("x-delivery-count", 4));

        Assert.Equal(1, stamp.CountOf(delivery, RabbitMqWire.ToEnvelope(Options(Key), delivery)));
    }

    [Fact]
    public void AStampedRedelivery_AddsTheBrokersCount_ToItsSignedCount()
    {
        var stamp = new DeliveryStamp(Key);
        var sent = new MessageEnvelope { Id = "m-1", Name = "orders.placed", Body = Encoding.UTF8.GetBytes("{}") };
        BasicProperties properties = RabbitMqWire.ToProperties(sent, 3, deliveryStamp: stamp.Sign(sent.Name, sent.Id, 3));
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [RabbitMqWire.DeliveryStampHeader] = Encoding.UTF8.GetBytes((string)properties.Headers![RabbitMqWire.DeliveryStampHeader]!),
            ["x-delivery-count"] = 2L,
        };
        var delivery = new BasicDeliverEventArgs(
            "consumer", 1, true, string.Empty, "billing", new BasicProperties(properties) { Headers = headers }, sent.Body);

        Assert.Equal(5, stamp.CountOf(delivery, RabbitMqWire.ToEnvelope(Options(Key), delivery)));
    }

    private static RabbitMqTransportOptions Options(string key) =>
        new RabbitMqTransportOptions { Exchange = "shop", ServiceName = "billing", DeliveryStampKey = key };

    private static MessageEnvelope Envelope(string id) => new MessageEnvelope
    {
        Id = id,
        Name = "rmq.order-placed",
        Body = Encoding.UTF8.GetBytes(Failing),
    };

    // A delivery through the service's own exchange, with no stamp, as the broker hands it over.
    private static BasicDeliverEventArgs Redelivery(bool redelivered, params (string Name, object? Value)[] headers)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string name, object? value) in headers)
        {
            map[name] = value;
        }
        var properties = new BasicProperties { MessageId = "m-1", Headers = map };
        return new BasicDeliverEventArgs("consumer", 1, redelivered, "shop", "orders.placed", properties, Encoding.UTF8.GetBytes("{}"));
    }

    /// <summary>A consumer over an in-memory transport with no native delay and the given unsettled window.</summary>
    private sealed class ConsumerHost : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private ConsumerHost(ServiceProvider services, HoldingClock clock, WindowTransport transport, Countdown settled)
        {
            _services = services;
            Clock = clock;
            Transport = transport;
            Settled = settled;
        }

        public HoldingClock Clock { get; }

        public WindowTransport Transport { get; }

        /// <summary>Counts each wait a delivery starts, each nack and each deferral.</summary>
        public Countdown Settled { get; }

        public MessageProbe Probe => _services.GetRequiredService<MessageProbe>();

        public static ConsumerHost Create(int window, int maxAttempts, bool fireAtOnce = false, IMessageInbox? inbox = null)
        {
            var settled = new Countdown();
            var clock = new HoldingClock(settled, fireAtOnce);
            var transport = new WindowTransport(new InMemoryTransport(clock), window, settled);
            var collection = new ServiceCollection();
            collection.AddSingleton<TimeProvider>(clock);
            collection.AddSingleton<MessageProbe>();
            collection.AddSingleton<IMessageTransport>(transport);
            if (inbox is not null)
            {
                collection.AddSingleton(inbox);
            }
            collection.AddRogue();
            collection.AddRogueMessaging(options =>
            {
                options.SerializerContext = TestMessagesJson.Default;
                options.RetryDelay = TimeSpan.FromMinutes(1);
                options.MaxRetryDelay = TimeSpan.FromMinutes(1);
                options.MaxAttempts = maxAttempts;
                options.MaxConcurrency = 8;
            });
            return new ConsumerHost(collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), clock, transport, settled);
        }

        public ValueTask SendAsync(params MessageEnvelope[] envelopes) => Transport.SendAsync(envelopes, CancellationToken.None);

        public Task<int> RunOnceAsync() => _services.GetRequiredService<MessageConsumer>().RunOnceAsync();

        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }

    /// <summary>Completes a wait once a count of events is reached, so a test waits on what happened, not on time.</summary>
    private sealed class Countdown
    {
        private readonly object _gate = new object();
        private readonly List<(int Target, TaskCompletionSource Done)> _waits = new List<(int Target, TaskCompletionSource Done)>();
        private int _count;

        public void Signal()
        {
            lock (_gate)
            {
                _count++;
                foreach ((int target, TaskCompletionSource done) in _waits.Where(w => w.Target <= _count))
                {
                    done.TrySetResult();
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

    /// <summary>An in-memory transport with no native delay that reports an unsettled window, records each nack and counts each deferral.</summary>
    private sealed class WindowTransport : IMessageTransport
    {
        private readonly Countdown _settled;

        public WindowTransport(InMemoryTransport inner, int window, Countdown settled)
        {
            Inner = inner;
            UnsettledLimit = window;
            _settled = settled;
        }

        public InMemoryTransport Inner { get; }

        public int? UnsettledLimit { get; }

        public TransportCapabilities Capabilities => TransportCapabilities.NativeDeadLetter;

        public ConcurrentQueue<TimeSpan> NackDelays { get; } = new ConcurrentQueue<TimeSpan>();

        public ValueTask SendAsync(IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken) => Inner.SendAsync(messages, cancellationToken);

        public ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxCount, CancellationToken cancellationToken) => Inner.ReceiveAsync(maxCount, cancellationToken);

        public ValueTask AckAsync(ReceivedMessage message, CancellationToken cancellationToken) => Inner.AckAsync(message, cancellationToken);

        public async ValueTask NackAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken)
        {
            await Inner.NackAsync(message, delay, cancellationToken);
            NackDelays.Enqueue(delay);
            _settled.Signal();
        }

        public async ValueTask DeferAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken)
        {
            await Inner.DeferAsync(message, delay, cancellationToken);
            _settled.Signal();
        }

        public ValueTask DeadLetterAsync(ReceivedMessage message, string reason, CancellationToken cancellationToken) => Inner.DeadLetterAsync(message, reason, cancellationToken);
    }

    /// <summary>
    /// A clock that records each timer made from it, which fires only when a test fires it, or at once
    /// when told to, so a test that should see no wait does not hang on one.
    /// </summary>
    private sealed class HoldingClock : TimeProvider
    {
        private readonly object _gate = new object();
        private readonly List<(TimerCallback Callback, object? State)> _pending = new List<(TimerCallback Callback, object? State)>();
        private readonly Countdown _settled;
        private readonly bool _fireAtOnce;

        public HoldingClock(Countdown settled, bool fireAtOnce)
        {
            _settled = settled;
            _fireAtOnce = fireAtOnce;
        }

        public ConcurrentQueue<TimeSpan> Timers { get; } = new ConcurrentQueue<TimeSpan>();

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void FireAll()
        {
            (TimerCallback Callback, object? State)[] due;
            lock (_gate)
            {
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
            lock (_gate)
            {
                _pending.Add((callback, state));
            }
            Timers.Enqueue(dueTime);
            _settled.Signal();
            if (_fireAtOnce)
            {
                FireAll();
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
