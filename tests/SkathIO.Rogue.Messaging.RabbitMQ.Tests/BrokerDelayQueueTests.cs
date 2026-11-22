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
/// A deferred (busy) delivery waits on the broker, not in the consumer: the transport puts its copy in a
/// wait queue of the service's own, whose message TTL dead-letters it back to the service's queue. The
/// copy keeps its signed delivery count, holds no prefetch place while it waits, and a copy the broker
/// refuses is not handed back to the consumer in process. Against a real broker in a container.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class BrokerDelayQueueTests
{
    private const string MessageName = "rmq.order-placed";

    // Only a bound on how long a broken test waits; no assertion depends on time.
    private static readonly TimeSpan _giveUpAfter = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan _shortDelay = TimeSpan.FromSeconds(1);

    private readonly RabbitMqFixture _broker;

    public BrokerDelayQueueTests(RabbitMqFixture broker) => _broker = broker;

    [RabbitMqTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ADeferredCopy_ComesBackToTheServiceQueue_WithItsSignedCountUnchanged(int failuresFirst)
    {
        RabbitMqTransportOptions options = Options(Guid.NewGuid().ToString("N"));
        await using var transport = new RabbitMqTransport(options, new[] { MessageName });
        await transport.SendAsync(new[] { Envelope("deferred") }, CancellationToken.None);
        ReceivedMessage delivery = await ReceiveAsync(transport, "deferred");
        for (int i = 0; i < failuresFirst; i++)
        {
            await transport.NackAsync(delivery, TimeSpan.Zero, CancellationToken.None);
            delivery = await ReceiveAsync(transport, "deferred");
        }
        Assert.Equal(1 + failuresFirst, delivery.DeliveryCount);

        await transport.DeferAsync(delivery, _shortDelay, CancellationToken.None);

        ReceivedMessage back = await ReceiveAsync(transport, "deferred");
        Assert.Equal(1 + failuresFirst, back.DeliveryCount);
        await transport.AckAsync(back, CancellationToken.None);
    }

    [RabbitMqFact]
    public async Task ADeferredCopy_WaitsInAQueueOfTheServicesOwn_ThatExpiresItBackToTheServiceQueue()
    {
        string run = Guid.NewGuid().ToString("N");
        RabbitMqTransportOptions billing = Options(run, "billing");
        RabbitMqTransportOptions shipping = Options(run, "shipping");

        string billingWait = await WaitQueueOfAsync(billing);
        string shippingWait = await WaitQueueOfAsync(shipping);

        Assert.NotEqual(billingWait, shippingWait);
        foreach (string queue in new[] { billing.ServiceName!, billing.DeadLetterQueue!, shipping.ServiceName!, shipping.DeadLetterQueue! })
        {
            Assert.NotEqual(queue, billingWait);
            Assert.NotEqual(queue, shippingWait);
        }
    }

    [RabbitMqTheory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(4, 2)]
    [InlineData(4, 3)]
    [InlineData(4, 4)]
    public async Task BusyDuplicates_AreNotWaitedOutInTheConsumer_AtAnyPrefetchCount(int prefetchCount, int duplicates)
    {
        var clock = new CountingClock();
        string run = Guid.NewGuid().ToString("N");
        await using ServiceProvider services = BusyConsumer(Options(run, prefetchCount: prefetchCount), clock);
        var envelopes = new MessageEnvelope[duplicates];
        for (int i = 0; i < duplicates; i++)
        {
            envelopes[i] = Envelope("dup-1");
        }
        await services.GetRequiredService<IMessageTransport>().SendAsync(envelopes, CancellationToken.None);

        int received = 0;
        DateTime giveUp = DateTime.UtcNow + _giveUpAfter;
        while (received < duplicates)
        {
            Assert.True(DateTime.UtcNow < giveUp, "Gave up waiting for the duplicates; received " + received + ".");
            received += await services.GetRequiredService<MessageConsumer>().RunOnceAsync();
        }

        Assert.Equal(0, clock.TimerCount);
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            Assert.Null(await raw.BasicGetAsync("service-" + run + ".dead-letter", true));
        }
    }

    [RabbitMqFact]
    public async Task ARefusedDeferralCopy_IsNotHandedBackToTheConsumer_InProcess()
    {
        RabbitMqTransportOptions options = Options(Guid.NewGuid().ToString("N"), prefetchCount: 1);
        string wait = await WaitQueueOfAsync(options);
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await using var transport = new RabbitMqTransport(options, new[] { MessageName });
            await transport.SendAsync(new[] { Envelope("connect", "rmq.report") }, CancellationToken.None);
            await RefuseEveryPublishAsync(raw, wait, options.ServiceName!);
            await transport.SendAsync(new[] { Envelope("refused") }, CancellationToken.None);
            ReceivedMessage delivery = await ReceiveAsync(transport, "refused");

            Exception? failure = await DeferQuietlyAsync(transport, delivery);

            Assert.DoesNotContain(RabbitMqHost.DeliveryStampKey, failure?.ToString() ?? string.Empty, StringComparison.Ordinal);
            IReadOnlyList<ReceivedMessage> next = await transport.ReceiveAsync(1, CancellationToken.None);
            Assert.DoesNotContain(next, d => d.Envelope.Id == "refused");
        }
    }

    private RabbitMqTransportOptions Options(string run, string service = "service", int prefetchCount = 100) => new RabbitMqTransportOptions
    {
        ConnectionUri = _broker.ConnectionUri,
        Exchange = "test-" + run,
        ServiceName = service + "-" + run,
        DeliveryStampKey = RabbitMqHost.DeliveryStampKey,
        PrefetchCount = prefetchCount,
    };

    private static MessageEnvelope Envelope(string id, string name = MessageName) => new MessageEnvelope
    {
        Id = id,
        Name = name,
        Body = Encoding.UTF8.GetBytes("{\"Id\":1,\"Note\":\"ok\"}"),
    };

    // Defers one delivery, stops consuming, and takes the copy off the service's queue once the broker
    // has moved it back there: its x-death header names the queue it expired from.
    private async Task<string> WaitQueueOfAsync(RabbitMqTransportOptions options)
    {
        await using (var transport = new RabbitMqTransport(options, new[] { MessageName }))
        {
            await transport.SendAsync(new[] { Envelope("probe") }, CancellationToken.None);
            await transport.DeferAsync(await ReceiveAsync(transport, "probe"), _shortDelay, CancellationToken.None);
        }
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            BasicGetResult back = await GetAsync(raw, options.ServiceName!);
            Assert.Equal("expired", DeathField(back, "reason"));
            return DeathField(back, "queue");
        }
    }

    // The wait queue is made again to hold one message, refusing any publish past that, and filled, so
    // the next deferral copy is refused. Deleting it also drops any binding that routed copies to it, so
    // a copy routed through an exchange is refused as unroutable.
    private static async Task RefuseEveryPublishAsync(IChannel raw, string wait, string serviceQueue)
    {
        await raw.QueueDeleteAsync(wait);
        await raw.QueueDeclareAsync(wait, true, false, false, new Dictionary<string, object?>
        {
            ["x-max-length"] = 1,
            ["x-overflow"] = "reject-publish",
            ["x-message-ttl"] = 3_600_000,
            ["x-dead-letter-exchange"] = string.Empty,
            ["x-dead-letter-routing-key"] = serviceQueue,
        });
        await raw.BasicPublishAsync(string.Empty, wait, Encoding.UTF8.GetBytes("filler"));
    }

    private static async Task<Exception?> DeferQuietlyAsync(RabbitMqTransport transport, ReceivedMessage delivery)
    {
        try
        {
            await transport.DeferAsync(delivery, _shortDelay, CancellationToken.None);
            return null;
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            return ex;
        }
    }

    private static string DeathField(BasicGetResult result, string field)
    {
        IDictionary<string, object?>? headers = result.BasicProperties.Headers;
        Assert.NotNull(headers);
        Assert.True(headers!.TryGetValue("x-death", out object? deaths), "The copy was not dead-lettered back by the broker.");
        IList<object?> list = Assert.IsAssignableFrom<IList<object?>>(deaths);
        IDictionary<string, object?> first = Assert.IsAssignableFrom<IDictionary<string, object?>>(list[0]);
        return first[field] switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            object other => other.ToString() ?? string.Empty,
            null => string.Empty,
        };
    }

    private static async Task<BasicGetResult> GetAsync(IChannel raw, string queue)
    {
        DateTime giveUp = DateTime.UtcNow + _giveUpAfter;
        while (true)
        {
            Assert.True(DateTime.UtcNow < giveUp, "Gave up waiting for the copy on " + queue + ".");
            if (await raw.BasicGetAsync(queue, true) is { } taken)
            {
                return taken;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200));
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

    // A consumer over the RabbitMQ transport whose inbox always says busy, with a retry delay long
    // enough that a wait in the consumer would show as a timer.
    private static ServiceProvider BusyConsumer(RabbitMqTransportOptions transportOptions, CountingClock clock)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<TimeProvider>(clock);
        collection.AddSingleton<MessageProbe>();
        collection.AddSingleton<IMessageInbox>(new AlwaysBusyInbox());
        collection.AddRogue();
        collection.AddRogueMessaging(static options =>
        {
            options.SerializerContext = TestMessagesJson.Default;
            options.RetryDelay = TimeSpan.FromHours(1);
            options.MaxRetryDelay = TimeSpan.FromHours(1);
            options.MaxAttempts = 3;
            options.MaxConcurrency = 8;
        });
        collection.AddRogueRabbitMqTransport(options =>
        {
            options.ConnectionUri = transportOptions.ConnectionUri;
            options.Exchange = transportOptions.Exchange;
            options.ServiceName = transportOptions.ServiceName;
            options.DeliveryStampKey = transportOptions.DeliveryStampKey;
            options.PrefetchCount = transportOptions.PrefetchCount;
        });
        return collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class AlwaysBusyInbox : IMessageInbox
    {
        public ValueTask<InboxClaimResult> TryClaimAsync(string messageId, CancellationToken cancellationToken)
            => new ValueTask<InboxClaimResult>(new InboxClaimResult(InboxClaim.Busy, 0));

        public ValueTask CompleteAsync(string messageId, long claimToken, CancellationToken cancellationToken) => default;

        public ValueTask ReleaseAsync(string messageId, long claimToken, CancellationToken cancellationToken) => default;
    }

    /// <summary>Counts each timer made from it, and fires each at once so a wait never hangs the test.</summary>
    private sealed class CountingClock : TimeProvider
    {
        private int _timers;

        public int TimerCount => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timers);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new SpentTimer();
        }

        private sealed class SpentTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => default;
        }
    }
}
