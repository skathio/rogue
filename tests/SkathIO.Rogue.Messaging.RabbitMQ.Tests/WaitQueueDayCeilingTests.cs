using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// The wait queue holds a copy for at most 24 hours: that is its own message TTL, the longest delay a
/// deferral copy carries, and the longest busy deferral delay the options allow. The options that set
/// how long a busy delivery stays away are checked at, just under and just past 24 hours. The broker
/// refuses a redeclare whose TTL differs from the queue's, which is how the queue's TTL is read here.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class WaitQueueDayCeilingTests
{
    private const string MessageName = "rmq.order-placed";

    private const int DayInMilliseconds = 86_400_000;

    // Only a bound on how long a broken test waits; no assertion depends on time.
    private static readonly TimeSpan _giveUpAfter = TimeSpan.FromSeconds(40);

    private static readonly TimeSpan _day = TimeSpan.FromHours(24);

    private readonly RabbitMqFixture _broker;

    public WaitQueueDayCeilingTests(RabbitMqFixture broker) => _broker = broker;

    [RabbitMqFact]
    public async Task TheWaitQueue_HasTwentyFourHours_AsItsMessageTtl()
    {
        RabbitMqTransportOptions options = await DeclaredAsync();

        Exception? refused = await RedeclareAsync(options, DayInMilliseconds);

        Assert.Null(refused);
    }

    [RabbitMqFact]
    public async Task TheWaitQueue_NoLongerHasTheLargestInt_AsItsMessageTtl()
    {
        RabbitMqTransportOptions options = await DeclaredAsync();

        Exception? refused = await RedeclareAsync(options, int.MaxValue);

        Assert.IsType<OperationInterruptedException>(refused);
    }

    [RabbitMqTheory]
    [InlineData(863_999_990_000L, "86399999")]
    [InlineData(863_999_999_999L, "86400000")]
    [InlineData(864_000_000_000L, "86400000")]
    public async Task ADelayUpToTwentyFourHours_IsCarriedWhole_AsTheCopysExpiration(long delayTicks, string expiration)
    {
        RabbitMqTransportOptions options = Options();
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await using var transport = new RabbitMqTransport(options, new[] { MessageName });
            await transport.SendAsync(new[] { Envelope("day") }, CancellationToken.None);

            await transport.DeferAsync(await ReceiveAsync(transport, "day"), TimeSpan.FromTicks(delayTicks), CancellationToken.None);

            BasicGetResult waiting = await GetAsync(raw, options.WaitQueue!);
            Assert.Equal("day", waiting.BasicProperties.MessageId);
            Assert.Equal(expiration, waiting.BasicProperties.Expiration);
        }
    }

    public static TheoryData<long> UpToADay => new TheoryData<long>
    {
        TimeSpan.FromHours(24).Ticks - 1,
        TimeSpan.FromHours(24).Ticks,
    };

    [Theory]
    [MemberData(nameof(UpToADay))]
    public void ARetryDelay_UpToTwentyFourHours_IsAccepted(long ticks)
    {
        Exception? thrown = Record.Exception(() => Configure(options =>
        {
            options.RetryDelay = TimeSpan.FromTicks(ticks);
            options.MaxRetryDelay = TimeSpan.FromTicks(ticks);
        }));

        Assert.Null(thrown);
    }

    [Fact]
    public void ARetryDelay_PastTwentyFourHours_IsRefused()
    {
        TimeSpan past = _day + TimeSpan.FromTicks(1);

        ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => Configure(options =>
        {
            options.RetryDelay = past;
            options.MaxRetryDelay = past;
        }));

        Assert.Contains(thrown.ParamName, new[] { nameof(MessagingOptions.RetryDelay), nameof(MessagingOptions.MaxRetryDelay) });
    }

    // With no retry delay a busy delivery stays away for the poll interval, so it feeds the busy delay.
    [Theory]
    [MemberData(nameof(UpToADay))]
    public void APollInterval_UpToTwentyFourHours_IsAccepted(long ticks)
    {
        Exception? thrown = Record.Exception(() => Configure(options =>
        {
            options.RetryDelay = TimeSpan.Zero;
            options.PollInterval = TimeSpan.FromTicks(ticks);
        }));

        Assert.Null(thrown);
    }

    [Fact]
    public void APollInterval_PastTwentyFourHours_IsRefused()
    {
        ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => Configure(options =>
        {
            options.RetryDelay = TimeSpan.Zero;
            options.PollInterval = _day + TimeSpan.FromTicks(1);
        }));

        Assert.Equal(nameof(MessagingOptions.PollInterval), thrown.ParamName);
    }

    [Fact]
    public void TheDocs_SayAStrayMessageLeavesTheWaitQueueWithinTwentyFourHours()
    {
        string[] paragraphs = DocParagraphs();

        Assert.Contains(paragraphs, static p => p.Contains("no expiration", StringComparison.Ordinal) && p.Contains("24 hours", StringComparison.Ordinal));
        Assert.DoesNotContain(paragraphs, static p => p.Contains("int.MaxValue", StringComparison.Ordinal) || p.Contains("24.8 days", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDocs_SayQueueArgumentsCannotChangeAfterDeclaration()
    {
        string[] paragraphs = DocParagraphs();

        Assert.Contains(paragraphs, static p =>
            p.Contains("argument", StringComparison.OrdinalIgnoreCase)
            && p.Contains("declar", StringComparison.OrdinalIgnoreCase)
            && (p.Contains("cannot", StringComparison.OrdinalIgnoreCase) || p.Contains("can't", StringComparison.OrdinalIgnoreCase) || p.Contains("can not", StringComparison.OrdinalIgnoreCase)));
    }

    private static void Configure(Action<MessagingOptions> configure) => new ServiceCollection().AddRogueMessaging(configure);

    private static string[] DocParagraphs()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "messaging-rabbitmq.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        return text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(static p => p.Replace('\n', ' ')).ToArray();
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SkathIO.Rogue.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("SkathIO.Rogue.sln not found above " + AppContext.BaseDirectory);
    }

    // Sending connects the transport, which declares its topology; nothing is bound to this name.
    private async Task<RabbitMqTransportOptions> DeclaredAsync()
    {
        RabbitMqTransportOptions options = Options();
        await using var transport = new RabbitMqTransport(options, new[] { MessageName });
        await transport.SendAsync(new[] { new MessageEnvelope { Id = "connect", Name = "rmq.report", Body = Encoding.UTF8.GetBytes("{}") } }, CancellationToken.None);
        return options;
    }

    private async Task<Exception?> RedeclareAsync(RabbitMqTransportOptions options, int messageTtl)
    {
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            try
            {
                await raw.QueueDeclareAsync(options.WaitQueue!, true, false, false, new Dictionary<string, object?>
                {
                    ["x-queue-type"] = "quorum",
                    ["x-dead-letter-exchange"] = string.Empty,
                    ["x-dead-letter-routing-key"] = options.ServiceName!,
                    ["x-dead-letter-strategy"] = "at-least-once",
                    ["x-overflow"] = "reject-publish",
                    ["x-message-ttl"] = messageTtl,
                });
                return null;
            }
            catch (OperationInterruptedException ex)
            {
                return ex;
            }
        }
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

    private static MessageEnvelope Envelope(string id) => new MessageEnvelope
    {
        Id = id,
        Name = MessageName,
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
