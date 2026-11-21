using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// A deferral with no delay, as on a shutdown release, goes straight back to the service's queue and
/// not through the wait queue, so it never waits behind a busy copy with a long delay; its signed
/// count stays the same, since a deferral is not an attempt. Any longer delay still waits in the wait
/// queue, and failed retries never use it. Against a real broker in a container, except the checks of
/// the docs.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class ZeroDelayDeferralTests
{
    private const string MessageName = "rmq.order-placed";

    // Only a bound on how long a broken test waits; no assertion depends on time.
    private static readonly TimeSpan _giveUpAfter = TimeSpan.FromSeconds(40);

    // Far longer than any test waits, so a copy with this delay stays at the head of the wait queue.
    private static readonly TimeSpan _oneHour = TimeSpan.FromHours(1);

    private readonly RabbitMqFixture _broker;

    public ZeroDelayDeferralTests(RabbitMqFixture broker) => _broker = broker;

    [RabbitMqFact]
    public async Task AZeroDelayDeferral_BehindALongBusyCopy_ComesBackAtOnce_WithItsSignedCountUnchanged()
    {
        RabbitMqTransportOptions options = Options();
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await using var transport = new RabbitMqTransport(options, new[] { MessageName });
            await DeferBusyForAnHourAsync(transport);
            await transport.SendAsync(new[] { Envelope("released") }, CancellationToken.None);
            await transport.NackAsync(await ReceiveAsync(transport, "released"), TimeSpan.Zero, CancellationToken.None);
            ReceivedMessage second = await ReceiveAsync(transport, "released");
            Assert.Equal(2, second.DeliveryCount);

            await transport.DeferAsync(second, TimeSpan.Zero, CancellationToken.None);

            ReceivedMessage back = await ReceiveAsync(transport, "released");
            Assert.Equal(2, back.DeliveryCount);
            await transport.AckAsync(back, CancellationToken.None);
            Assert.Equal(1u, await MessageCountAsync(raw, options.WaitQueue!));
        }
    }

    [RabbitMqFact]
    public async Task TheShortestNonZeroDelay_StillWaitsInTheWaitQueue()
    {
        RabbitMqTransportOptions options = Options();
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await using var transport = new RabbitMqTransport(options, new[] { MessageName });
            await DeferBusyForAnHourAsync(transport);
            await transport.SendAsync(new[] { Envelope("brief") }, CancellationToken.None);

            await transport.DeferAsync(await ReceiveAsync(transport, "brief"), TimeSpan.FromTicks(1), CancellationToken.None);

            await WaitForCountAsync(raw, options.WaitQueue!, 2u);
        }
    }

    [RabbitMqFact]
    public async Task AFailedRetry_WithALongDelay_StaysOffTheWaitQueue()
    {
        RabbitMqTransportOptions options = Options();
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await using var transport = new RabbitMqTransport(options, new[] { MessageName });
            await transport.SendAsync(new[] { Envelope("failed") }, CancellationToken.None);

            await transport.NackAsync(await ReceiveAsync(transport, "failed"), _oneHour, CancellationToken.None);

            ReceivedMessage back = await ReceiveAsync(transport, "failed");
            Assert.Equal(2, back.DeliveryCount);
            await transport.AckAsync(back, CancellationToken.None);
            Assert.Equal(0u, await MessageCountAsync(raw, options.WaitQueue!));
        }
    }

    [RabbitMqTheory]
    [InlineData(1L)]
    [InlineData(10_000_000L)]
    public async Task ADeferralWithADelay_WhoseWaitQueueCopyIsRefused_IsDeadLetteredWithItsCountAndAReason(long delayTicks)
    {
        RabbitMqTransportOptions options = Options();
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await using var transport = new RabbitMqTransport(options, new[] { MessageName });
            await transport.SendAsync(new[] { Envelope("connect", "rmq.report") }, CancellationToken.None);
            await FillWaitQueueAsync(raw, options);
            await transport.SendAsync(new[] { Envelope("refused") }, CancellationToken.None);
            await transport.NackAsync(await ReceiveAsync(transport, "refused"), TimeSpan.Zero, CancellationToken.None);
            ReceivedMessage second = await ReceiveAsync(transport, "refused");

            await DeferQuietlyAsync(transport, second, TimeSpan.FromTicks(delayTicks));

            BasicGetResult kept = await GetAsync(raw, options.DeadLetterQueue!);
            Assert.Equal("refused", kept.BasicProperties.MessageId);
            Assert.Equal(2, Convert.ToInt32(kept.BasicProperties.Headers![RabbitMqWire.DeliveryCountHeader], CultureInfo.InvariantCulture));
            Assert.Contains("wait queue refused", RabbitMqWire.HeaderString(kept.BasicProperties, RabbitMqWire.DeadLetterReasonHeader), StringComparison.Ordinal);
        }
    }

    [RabbitMqFact]
    public async Task AZeroDelayDeferral_WhoseServiceQueueCopyIsRefused_ThrowsTheRefusal_AndTheMessageIsKept()
    {
        RabbitMqTransportOptions options = Options();
        options.PrefetchCount = 1;
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await using var transport = new RabbitMqTransport(options, new[] { MessageName });
            await LimitQueueToOneAsync(transport, raw, options);
            await transport.SendAsync(new[] { Envelope("refused") }, CancellationToken.None);
            ReceivedMessage first = await ReceiveAsync(transport, "refused");
            await FillQueueAsync(transport);

            await Assert.ThrowsAnyAsync<Exception>(async () => await transport.DeferAsync(first, TimeSpan.Zero, CancellationToken.None));

            Assert.True(await IsKeptAsync(transport, raw, options, "refused"), "The refused message was neither dead-lettered nor back on the service's queue.");
        }
    }

    [Fact]
    public void TheDocs_NoLongerSay_AZeroDelayDeferralGoesThroughTheWaitQueue()
    {
        string remarks = Normalized("src/SkathIO.Rogue.Messaging.RabbitMQ/RabbitMqTransport.cs");
        string docs = Normalized("docs/messaging-rabbitmq.md");
        string changelog = Normalized("CHANGELOG.md");

        Assert.DoesNotContain("and so does a message deferred with no delay", remarks, StringComparison.Ordinal);
        Assert.DoesNotContain("goes through the wait queue too", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("goes through the wait queue too", changelog, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocs_SayFailedRetriesStayOffTheWaitQueue_BecauseItExpiresOnlyFromItsHead()
    {
        string docs = File.ReadAllText(Path.Combine(RepoRoot(), "docs/messaging-rabbitmq.md")).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(docs.Split("\n\n"), paragraph =>
        {
            string text = Regex.Replace(paragraph, @"\s+", " ");
            return text.Contains("retr", StringComparison.OrdinalIgnoreCase)
                && text.Contains("wait queue", StringComparison.OrdinalIgnoreCase)
                && text.Contains("head", StringComparison.OrdinalIgnoreCase);
        });
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

    // A busy copy deferred for an hour holds the head of the wait queue for longer than any test runs.
    private static async Task DeferBusyForAnHourAsync(RabbitMqTransport transport)
    {
        await transport.SendAsync(new[] { Envelope("busy") }, CancellationToken.None);
        await transport.DeferAsync(await ReceiveAsync(transport, "busy"), _oneHour, CancellationToken.None);
    }

    private static async Task<uint> MessageCountAsync(IChannel raw, string queue)
        => (await raw.QueueDeclarePassiveAsync(queue)).MessageCount;

    private static async Task WaitForCountAsync(IChannel raw, string queue, uint expected)
    {
        DateTime giveUp = DateTime.UtcNow + _giveUpAfter;
        while (await MessageCountAsync(raw, queue) != expected)
        {
            Assert.True(DateTime.UtcNow < giveUp, "Gave up waiting for " + expected + " messages on " + queue + ".");
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }

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
        await raw.QueueBindAsync(queue, options.Exchange, MessageName);
    }

    // Sends more messages until the broker refuses one, so the service's queue refuses the put-back copy
    // too. How far past the limit a quorum queue lets it go is the broker's choice.
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

    // A refusal is reported as the transport chooses; only where the message goes is checked.
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

    // Kept means dead-lettered, or back on the service's queue; which of the two is the transport's choice.
    private static async Task<bool> IsKeptAsync(RabbitMqTransport transport, IChannel raw, RabbitMqTransportOptions options, string id)
    {
        DateTime giveUp = DateTime.UtcNow + _giveUpAfter;
        while (DateTime.UtcNow < giveUp)
        {
            if (await IsDeadLetteredAsync(raw, options, id) || await IsReceivedAsync(transport, id))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> IsDeadLetteredAsync(IChannel raw, RabbitMqTransportOptions options, string id)
        => await raw.BasicGetAsync(options.DeadLetterQueue!, true) is { } dead && dead.BasicProperties.MessageId == id;

    // Acks whatever it receives, so the service's queue empties as the loop goes.
    private static async Task<bool> IsReceivedAsync(RabbitMqTransport transport, string id)
    {
        bool found = false;
        foreach (ReceivedMessage delivery in await transport.ReceiveAsync(1, CancellationToken.None))
        {
            await transport.AckAsync(delivery, CancellationToken.None);
            found |= delivery.Envelope.Id == id;
        }
        return found;
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

    private static string Normalized(string relativePath)
        => Regex.Replace(File.ReadAllText(Path.Combine(RepoRoot(), relativePath)), @"[\s/]+", " ");

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SkathIO.Rogue.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("SkathIO.Rogue.sln was not found above the test output.");
    }
}
