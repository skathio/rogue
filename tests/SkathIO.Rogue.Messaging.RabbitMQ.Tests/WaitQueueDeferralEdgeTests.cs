using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// The ends of a deferral's delay on the RabbitMQ transport: none, the shortest, and past the 24
/// hours the broker is given at most, which are held to 24 hours. A delay is carried as whole milliseconds, rounded up; a copy keeps its
/// signed count whatever the delay, and a missing or foreign message or a negative delay is refused.
/// Against a real broker in a container, except the argument checks.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class WaitQueueDeferralEdgeTests
{
    private const string MessageName = "rmq.order-placed";

    // Only a bound on how long a broken test waits; no assertion depends on time.
    private static readonly TimeSpan _giveUpAfter = TimeSpan.FromSeconds(40);

    private static readonly Uri _nowhere = new Uri("amqp://127.0.0.1:1/");

    private readonly RabbitMqFixture _broker;

    public WaitQueueDeferralEdgeTests(RabbitMqFixture broker) => _broker = broker;

    [RabbitMqFact]
    public async Task ADeferralWithNoDelay_ComesBack_WithItsSignedCountUnchanged()
    {
        RabbitMqTransportOptions options = Options();
        await using var transport = new RabbitMqTransport(options, new[] { MessageName });
        await transport.SendAsync(new[] { Envelope("released") }, CancellationToken.None);
        await transport.NackAsync(await ReceiveAsync(transport, "released"), TimeSpan.Zero, CancellationToken.None);
        ReceivedMessage second = await ReceiveAsync(transport, "released");
        Assert.Equal(2, second.DeliveryCount);

        await transport.DeferAsync(second, TimeSpan.Zero, CancellationToken.None);

        ReceivedMessage back = await ReceiveAsync(transport, "released");
        Assert.Equal(2, back.DeliveryCount);
        await transport.AckAsync(back, CancellationToken.None);
    }

    [RabbitMqFact]
    public async Task TheShortestDelay_StillComesBack_WithItsCount()
    {
        RabbitMqTransportOptions options = Options();
        await using var transport = new RabbitMqTransport(options, new[] { MessageName });
        await transport.SendAsync(new[] { Envelope("brief") }, CancellationToken.None);

        await transport.DeferAsync(await ReceiveAsync(transport, "brief"), TimeSpan.FromTicks(1), CancellationToken.None);

        ReceivedMessage back = await ReceiveAsync(transport, "brief");
        Assert.Equal(1, back.DeliveryCount);
        await transport.AckAsync(back, CancellationToken.None);
    }

    [RabbitMqTheory]
    [InlineData(36_000_000_000L, "3600000")]
    [InlineData(36_000_000_001L, "3600001")]
    [InlineData(864_000_000_001L, "86400000")]
    [InlineData(864_000_010_000L, "86400000")]
    [InlineData(21_474_836_470_000L, "86400000")]
    [InlineData(long.MaxValue, "86400000")]
    public async Task ADelay_IsCarriedAsWholeMilliseconds_RoundedUp_AndHeldToTheMost(long delayTicks, string expiration)
    {
        RabbitMqTransportOptions options = Options();
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await using var transport = new RabbitMqTransport(options, new[] { MessageName });
            await transport.SendAsync(new[] { Envelope("long") }, CancellationToken.None);

            await transport.DeferAsync(await ReceiveAsync(transport, "long"), TimeSpan.FromTicks(delayTicks), CancellationToken.None);

            BasicGetResult waiting = await GetAsync(raw, options.WaitQueue!);
            Assert.Equal("long", waiting.BasicProperties.MessageId);
            Assert.Equal(expiration, waiting.BasicProperties.Expiration);
            Assert.NotNull(RabbitMqWire.HeaderString(waiting.BasicProperties, RabbitMqWire.DeliveryStampHeader));
        }
    }

    [Fact]
    public async Task Deferring_WithoutAMessage_OrOneItDidNotDeliver_OrANegativeDelay_IsRefused()
    {
        await using var transport = new RabbitMqTransport(new RabbitMqTransportOptions { DeliveryStampKey = RabbitMqHost.DeliveryStampKey, ConnectionUri = _nowhere }, Array.Empty<string>());
        var foreign = new ReceivedMessage(new MessageEnvelope { Name = "orders.placed" }, 1);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await transport.DeferAsync(null!, TimeSpan.Zero, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await transport.DeferAsync(foreign, TimeSpan.FromTicks(-1), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.DeferAsync(foreign, TimeSpan.Zero, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.DeferAsync(foreign, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    private RabbitMqTransportOptions Options()
    {
        string run = Guid.NewGuid().ToString("N");
        return new RabbitMqTransportOptions
        {
            ConnectionUri = _broker.ConnectionUri,
            Exchange = "test-" + run,
            ServiceName = "service-" + run,
            DeliveryStampKey = RabbitMqHost.DeliveryStampKey,
        };
    }

    private static MessageEnvelope Envelope(string id, string name = MessageName) => new MessageEnvelope
    {
        Id = id,
        Name = name,
        Body = Encoding.UTF8.GetBytes("{\"Id\":1,\"Note\":\"ok\"}"),
    };

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
