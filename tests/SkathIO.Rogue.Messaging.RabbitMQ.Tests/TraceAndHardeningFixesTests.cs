using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// The trace and hardening fixes, held at the ends of their ranges: a retry delay past what one
/// Task.Delay accepts, a nack delay that runs past the end of time, the name a delivery on the
/// service's own exchange gets, the one name tag every unknown name shares, the size limit the
/// transport is given, and an inbox asked by message name as well as id.
/// </summary>
public sealed class TraceAndHardeningFixesTests
{
    private const string Failing = "{\"Id\":1,\"Note\":\"fail\"}";

    // A message delivered this many times before, with a first retry delay of at most 24 hours, has
    // doubled past every delay below, so its next failure is retried after MaxRetryDelay.
    private const int DeliveredBefore = 29;

    // The longest delay one Task.Delay accepts.
    private static readonly TimeSpan _longestWait = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    // The longest first retry delay the options allow.
    private static readonly TimeSpan _day = TimeSpan.FromHours(24);

    private static readonly RabbitMqTransportOptions _wire = new RabbitMqTransportOptions { Exchange = "shop", ServiceName = "billing" };

    public static TheoryData<long> DelayTicks() => new TheoryData<long>
    {
        TimeSpan.FromMilliseconds(1).Ticks,
        _longestWait.Ticks,
        (_longestWait + TimeSpan.FromMilliseconds(1)).Ticks,
        (_longestWait + _longestWait + TimeSpan.FromMilliseconds(1)).Ticks,
        TimeSpan.MaxValue.Ticks,
    };

    [Fact]
    public async Task WithoutNativeDelay_ARetryWithNoDelay_IsNackedAtOnce_WithoutWaiting()
    {
        await using ConsumerHost host = ConsumerHost.Create(TimeSpan.Zero);

        await host.DeliverAsync(Envelope("rmq.order-placed", Failing));

        Assert.Equal(TimeSpan.Zero, Assert.Single(host.Transport.NackDelays));
        Assert.Empty(host.Clock.Waited);
        Assert.Equal(2, Assert.Single(await host.Transport.ReceiveAsync(1, CancellationToken.None)).DeliveryCount);
    }

    [Theory]
    [MemberData(nameof(DelayTicks))]
    public async Task WithoutNativeDelay_ARetryDelay_IsWaitedInFull_InPartsNoLongerThanTaskDelayAccepts_ThenNacked(long ticks)
    {
        TimeSpan delay = TimeSpan.FromTicks(ticks);
        await using ConsumerHost host = ConsumerHost.Create(delay);

        await host.DeliverAgainAsync(Envelope("rmq.order-placed", Failing));

        TimeSpan[] waited = host.Clock.Waited.ToArray();
        Assert.NotEmpty(waited);
        Assert.All(waited, part => Assert.InRange(part, TimeSpan.FromMilliseconds(1), _longestWait));

        // A timer keeps whole milliseconds, so the parts add up to the delay less under a millisecond.
        decimal shortBy = delay.Ticks - waited.Sum(static part => (decimal)part.Ticks);
        Assert.InRange(shortBy, 0m, TimeSpan.TicksPerMillisecond - 1);
        Assert.Equal(TimeSpan.Zero, Assert.Single(host.Transport.NackDelays));
        Assert.Empty(host.Transport.Inner.DeadLettered);
        Assert.Equal(DeliveredBefore + 2, Assert.Single(await host.Transport.ReceiveAsync(1, CancellationToken.None)).DeliveryCount);
    }

    [Fact]
    public async Task WithoutNativeDelay_ADelayAtTheTaskDelayLimit_IsOneWait_AndOneMillisecondPast_IsTwo()
    {
        await using ConsumerHost atLimit = ConsumerHost.Create(_longestWait);
        await using ConsumerHost pastLimit = ConsumerHost.Create(_longestWait + TimeSpan.FromMilliseconds(1));

        await atLimit.DeliverAgainAsync(Envelope("rmq.order-placed", Failing));
        await pastLimit.DeliverAgainAsync(Envelope("rmq.order-placed", Failing));

        Assert.Equal(new[] { _longestWait }, atLimit.Clock.Waited.ToArray());
        Assert.Equal(new[] { _longestWait, TimeSpan.FromMilliseconds(1) }, pastLimit.Clock.Waited.ToArray());
    }

    [Fact]
    public async Task InMemoryTransport_NackedWithNoDelay_DeliversAgainAtOnce_CountedOneHigher()
    {
        var clock = new StepClock();
        var transport = new InMemoryTransport(clock);
        ReceivedMessage delivery = await SendAndReceiveAsync(transport);

        await transport.NackAsync(delivery, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(2, Assert.Single(await transport.ReceiveAsync(1, CancellationToken.None)).DeliveryCount);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    public async Task InMemoryTransport_ADueTimeAtOrPastTheEndOfTime_IsCapped_AndTheMessageIsHeldNotLost(long ticksPastTheEnd)
    {
        var clock = new StepClock();
        var transport = new InMemoryTransport(clock);
        ReceivedMessage delivery = await SendAndReceiveAsync(transport);
        TimeSpan delay = (DateTimeOffset.MaxValue - clock.GetUtcNow()) + TimeSpan.FromTicks(ticksPastTheEnd);

        await transport.NackAsync(delivery, delay, CancellationToken.None);

        // Settled once, and held rather than ready.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.AckAsync(delivery, CancellationToken.None));
        Assert.Empty(await transport.ReceiveAsync(1, CancellationToken.None));
        clock.Advance(TimeSpan.FromDays(365 * 100));
        Assert.Empty(await transport.ReceiveAsync(1, CancellationToken.None));
        Assert.Empty(transport.DeadLettered);
    }

    [Fact]
    public async Task InMemoryTransport_TheLongestNackDelay_DoesNotThrow_AndHoldsTheMessage()
    {
        var clock = new StepClock();
        var transport = new InMemoryTransport(clock);
        ReceivedMessage delivery = await SendAndReceiveAsync(transport);

        await transport.NackAsync(delivery, TimeSpan.MaxValue, CancellationToken.None);

        Assert.Empty(await transport.ReceiveAsync(1, CancellationToken.None));
    }

    [Theory]
    [InlineData("payments.refunded")]
    [InlineData("orders.placed")]
    [InlineData("")]
    [InlineData(null)]
    public void OnTheServicesOwnExchange_TheRoutingKeyNamesTheDelivery_NotTheSendersTypeProperty(string? type)
    {
        Assert.Equal("orders.placed", RabbitMqWire.ResolveName(_wire, "shop", "orders.placed", new BasicProperties { Type = type }));
    }

    [Fact]
    public void OnAnotherExchange_TheTypePropertyStillNamesTheDelivery()
    {
        Assert.Equal("orders.placed", RabbitMqWire.ResolveName(_wire, string.Empty, "billing", new BasicProperties { Type = "orders.placed" }));
    }

    [Fact]
    public void OnAnotherExchange_WithNoTypeProperty_TheNameIsEmpty()
    {
        Assert.Equal(string.Empty, RabbitMqWire.ResolveName(_wire, "elsewhere", "orders.placed", new BasicProperties()));
    }

    [Fact]
    public async Task EveryUnknownName_IsTaggedWithOneFixedValue_AndAKnownNameWithItself()
    {
        string first = "unknown-" + Guid.NewGuid().ToString("N");
        string second = "unknown-" + Guid.NewGuid().ToString("N");
        var nameTags = new ConcurrentQueue<string?>();
        using MeterListener listener = ListenToNameTags(nameTags);
        await using ConsumerHost host = ConsumerHost.Create(TimeSpan.Zero);

        await host.DeliverAsync(
            Envelope(first, "{}"),
            Envelope(second, "{}"),
            Envelope(string.Empty, "{}"),
            Envelope("rmq.order-placed", "{\"Id\":1,\"Note\":\"n\"}"));

        Assert.Equal(3, host.Transport.Inner.DeadLettered.Count);
        Assert.DoesNotContain(first, nameTags);
        Assert.DoesNotContain(second, nameTags);
        Assert.Contains("(unknown)", nameTags);
        Assert.Contains("rmq.order-placed", nameTags);
        Assert.All(nameTags, tag => Assert.Contains(tag, new[] { "(unknown)", "rmq.order-placed", "rmq.reserve", "rmq.report" }));
    }

    [Fact]
    public async Task TheInbox_IsAskedByMessageNameAndId_SoTheSameIdUnderTwoNamesRunsBoth()
    {
        var inbox = new NameAndIdInbox();
        await using ConsumerHost host = ConsumerHost.Create(TimeSpan.Zero, s => s.AddSingleton<IMessageInbox>(inbox));
        MessageEnvelope placed = Envelope("rmq.order-placed", "{\"Id\":1,\"Note\":\"n\"}");
        placed.Id = "shared-id";
        MessageEnvelope reserve = Envelope("rmq.reserve", "{\"Id\":2}");
        reserve.Id = "shared-id";

        await host.DeliverAsync(placed);
        await host.DeliverAsync(reserve);
        await host.DeliverAsync(placed);

        Assert.Equal(new[] { "placed:1", "reserve:2" }, host.Probe.Ran);
        Assert.Empty(inbox.IdOnlyCalls);
        Assert.Contains(("claim", "rmq.order-placed", "shared-id"), inbox.Calls);
        Assert.Contains(("complete", "rmq.order-placed", "shared-id"), inbox.Calls);
        Assert.Contains(("claim", "rmq.reserve", "shared-id"), inbox.Calls);
        Assert.Contains(("complete", "rmq.reserve", "shared-id"), inbox.Calls);
        Assert.Empty(host.Transport.Inner.DeadLettered);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Registration_GivesTheRabbitMqTransportTheMessagingSizeLimit_AtEitherEndOfItsRange(int limit)
    {
        using ServiceProvider provider = RabbitMqServices(o => o.MaxMessageBytes = limit);

        Assert.Equal(limit, provider.GetRequiredService<RabbitMqTransport>().MaxMessageBytes);
    }

    [Fact]
    public void Registration_WithTheSizeLimitLeftOut_GivesTheTransportTheMessagingDefault()
    {
        using ServiceProvider provider = RabbitMqServices(null);

        Assert.Equal(new MessagingOptions().MaxMessageBytes, provider.GetRequiredService<RabbitMqTransport>().MaxMessageBytes);
    }

    private static ServiceProvider RabbitMqServices(Action<MessagingOptions>? configure)
    {
        var services = new ServiceCollection();
        services.AddRogue();
        services.AddRogueMessaging(o =>
        {
            o.SerializerContext = TestMessagesJson.Default;
            configure?.Invoke(o);
        });
        services.AddRogueRabbitMqTransport(static o =>
        {
            o.Exchange = "shop";
            o.DeliveryStampKey = RabbitMqHost.DeliveryStampKey;
        });
        return services.BuildServiceProvider();
    }

    private static MeterListener ListenToNameTags(ConcurrentQueue<string?> nameTags)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == RogueMessagingTelemetry.Name && instrument.Name == "rogue.messaging.consumed")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "rogue.message.name")
                {
                    nameTags.Enqueue(tag.Value as string);
                }
            }
        });
        listener.Start();
        return listener;
    }

    private static async Task<ReceivedMessage> SendAndReceiveAsync(InMemoryTransport transport)
    {
        await transport.SendAsync(new[] { Envelope("rmq.order-placed", Failing) }, CancellationToken.None);
        return Assert.Single(await transport.ReceiveAsync(1, CancellationToken.None));
    }

    private static MessageEnvelope Envelope(string name, string json) =>
        new MessageEnvelope { Name = name, Body = Encoding.UTF8.GetBytes(json) };

    /// <summary>A consumer on an in-memory transport that has no native delay, as RabbitMQ has none, on a clock the test moves.</summary>
    private sealed class ConsumerHost : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private ConsumerHost(ServiceProvider services, StepClock clock, NoDelayTransport transport)
        {
            _services = services;
            Clock = clock;
            Transport = transport;
        }

        public StepClock Clock { get; }

        public NoDelayTransport Transport { get; }

        public MessageProbe Probe => _services.GetRequiredService<MessageProbe>();

        public static ConsumerHost Create(TimeSpan retryDelay, Action<IServiceCollection>? services = null)
        {
            var clock = new StepClock();
            var transport = new NoDelayTransport(new InMemoryTransport(clock));
            var collection = new ServiceCollection();
            collection.AddSingleton<TimeProvider>(clock);
            collection.AddSingleton<MessageProbe>();
            collection.AddSingleton<IMessageTransport>(transport);
            services?.Invoke(collection);
            collection.AddRogue();
            collection.AddRogueMessaging(options =>
            {
                options.SerializerContext = TestMessagesJson.Default;
                options.RetryDelay = retryDelay > _day ? _day : retryDelay;
                options.MaxRetryDelay = retryDelay;
                options.MaxAttempts = DeliveredBefore + 2;
            });
            return new ConsumerHost(collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), clock, transport);
        }

        public async Task DeliverAsync(params MessageEnvelope[] envelopes)
        {
            await Transport.SendAsync(envelopes, CancellationToken.None);
            await _services.GetRequiredService<MessageConsumer>().RunOnceAsync();
            Clock.Advance(TimeSpan.FromDays(1));
        }

        // Like DeliverAsync, but the message is first put back unhandled until it has been delivered
        // DeliveredBefore times, through the inner transport so no nack is recorded.
        public async Task DeliverAgainAsync(MessageEnvelope envelope)
        {
            await Transport.Inner.SendAsync(new[] { envelope }, CancellationToken.None);
            for (int i = 0; i < DeliveredBefore; i++)
            {
                ReceivedMessage delivery = Assert.Single(await Transport.Inner.ReceiveAsync(1, CancellationToken.None));
                await Transport.Inner.NackAsync(delivery, TimeSpan.Zero, CancellationToken.None);
            }
            await DeliverAsync();
        }

        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }

    /// <summary>An in-memory transport that reports no native delay and records each nack's delay.</summary>
    private sealed class NoDelayTransport : IMessageTransport
    {
        public NoDelayTransport(InMemoryTransport inner) => Inner = inner;

        public InMemoryTransport Inner { get; }

        public TransportCapabilities Capabilities => TransportCapabilities.NativeDeadLetter;

        public ConcurrentQueue<TimeSpan> NackDelays { get; } = new ConcurrentQueue<TimeSpan>();

        public ValueTask SendAsync(IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken) => Inner.SendAsync(messages, cancellationToken);

        public ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxCount, CancellationToken cancellationToken) => Inner.ReceiveAsync(maxCount, cancellationToken);

        public ValueTask AckAsync(ReceivedMessage message, CancellationToken cancellationToken) => Inner.AckAsync(message, cancellationToken);

        public ValueTask NackAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken)
        {
            NackDelays.Enqueue(delay);
            return Inner.NackAsync(message, delay, cancellationToken);
        }

        public ValueTask DeferAsync(ReceivedMessage message, TimeSpan delay, CancellationToken cancellationToken) => Inner.DeferAsync(message, delay, cancellationToken);

        public ValueTask DeadLetterAsync(ReceivedMessage message, string reason, CancellationToken cancellationToken) => Inner.DeadLetterAsync(message, reason, cancellationToken);
    }

    /// <summary>An inbox that keeps ids per message name, and records which overload the consumer called.</summary>
    private sealed class NameAndIdInbox : IMessageInbox
    {
        private readonly HashSet<(string Name, string Id)> _handled = new HashSet<(string Name, string Id)>();

        public ConcurrentQueue<(string Call, string Name, string Id)> Calls { get; } = new ConcurrentQueue<(string Call, string Name, string Id)>();

        public ConcurrentQueue<string> IdOnlyCalls { get; } = new ConcurrentQueue<string>();

        public ValueTask<InboxClaimResult> TryClaimAsync(string messageId, CancellationToken cancellationToken)
        {
            IdOnlyCalls.Enqueue("claim:" + messageId);
            return new ValueTask<InboxClaimResult>(new InboxClaimResult(InboxClaim.Claimed, 0));
        }

        public ValueTask CompleteAsync(string messageId, long claimToken, CancellationToken cancellationToken)
        {
            IdOnlyCalls.Enqueue("complete:" + messageId);
            return default;
        }

        public ValueTask ReleaseAsync(string messageId, long claimToken, CancellationToken cancellationToken)
        {
            IdOnlyCalls.Enqueue("release:" + messageId);
            return default;
        }

        public ValueTask<InboxClaimResult> TryClaimAsync(string messageName, string messageId, CancellationToken cancellationToken)
        {
            Calls.Enqueue(("claim", messageName, messageId));
            lock (_handled)
            {
                return new ValueTask<InboxClaimResult>(new InboxClaimResult(_handled.Contains((messageName, messageId)) ? InboxClaim.Handled : InboxClaim.Claimed, 0));
            }
        }

        public ValueTask CompleteAsync(string messageName, string messageId, long claimToken, CancellationToken cancellationToken)
        {
            Calls.Enqueue(("complete", messageName, messageId));
            lock (_handled)
            {
                _handled.Add((messageName, messageId));
            }
            return default;
        }

        public ValueTask ReleaseAsync(string messageName, string messageId, long claimToken, CancellationToken cancellationToken)
        {
            Calls.Enqueue(("release", messageName, messageId));
            return default;
        }
    }

    /// <summary>
    /// A clock that moves only when a test moves it. A timer made from it records its due time and fires
    /// at once, so a test sees each wait without waiting.
    /// </summary>
    private sealed class StepClock : TimeProvider
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
                _now = by > DateTimeOffset.MaxValue - _now ? DateTimeOffset.MaxValue : _now + by;
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
}
