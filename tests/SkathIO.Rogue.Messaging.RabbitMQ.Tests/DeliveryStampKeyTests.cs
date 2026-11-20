using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// The key that signs a retry copy's delivery count is a secret every instance of a service is given,
/// at least 32 bytes long, with no random default: so a copy one instance puts back is read with its
/// count by any other instance holding the same key, and a poison message reaches the attempt limit
/// however its deliveries are spread across instances. The key never shows in a log or an exception.
/// The broker tests are skipped without Docker and always run in CI.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class DeliveryStampKeyTests
{
    private const int MaxAttempts = 3;
    private const string MessageName = "rmq.order-placed";

    // Bounds on how long a broken broker test keeps trying; no assertion depends on time.
    private const int MaxReceiveTries = 120;

    private readonly RabbitMqFixture _broker;

    public DeliveryStampKeyTests(RabbitMqFixture broker) => _broker = broker;

    /// <summary>The key's length in bytes (null: never set), and whether registration accepts it.</summary>
    public static TheoryData<int?, bool> KeyLengths => new TheoryData<int?, bool>
    {
        { null, false },
        { 0, false },
        { 1, false },
        { 31, false },
        { 32, true },
        { 33, true },
        { 64, true },
    };

    [Theory]
    [MemberData(nameof(KeyLengths))]
    public void TheKey_IsRequired_AndAtLeast32Bytes_WhenTheTransportIsRegistered(int? length, bool accepted)
    {
        Exception? thrown = Record.Exception(() => new ServiceCollection().AddRogueRabbitMqTransport(o => SetKey(o, length)));

        AssertChecked(thrown, accepted);
    }

    [Theory]
    [MemberData(nameof(KeyLengths))]
    public void TheKey_IsRequired_AndAtLeast32Bytes_WhenTheTransportIsBuilt(int? length, bool accepted)
    {
        var options = new RabbitMqTransportOptions { ServiceName = "billing" };
        SetKey(options, length);

        Exception? thrown = Record.Exception(() => new RabbitMqTransport(options, Array.Empty<string>()));

        AssertChecked(thrown, accepted);
    }

    [Fact]
    public void AKeySetToNothing_IsRefused()
    {
        Exception? thrown = Record.Exception(() => new ServiceCollection().AddRogueRabbitMqTransport(static o => o.DeliveryStampKey = default!));

        Assert.IsAssignableFrom<ArgumentException>(thrown);
    }

    [Fact]
    public void ARefusedKey_IsNotInTheException()
    {
        StampKey key = StampKey.Of(31, seed: 5);

        Exception? viaRegistration = Record.Exception(() => new ServiceCollection().AddRogueRabbitMqTransport(o => o.DeliveryStampKey = key));
        Exception? viaTransport = Record.Exception(() => new RabbitMqTransport(new RabbitMqTransportOptions { DeliveryStampKey = key }, Array.Empty<string>()));

        AssertKeyAbsent(key, Assert.IsAssignableFrom<ArgumentException>(viaRegistration).ToString());
        AssertKeyAbsent(key, Assert.IsAssignableFrom<ArgumentException>(viaTransport).ToString());
    }

    /// <summary>
    /// One instance fails a delivery and puts its copy back; another instance of the same service reads
    /// the copy as delivery 2 when it holds the same key, and as delivery 1, unverified, when it does not.
    /// </summary>
    [RabbitMqTheory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task ACopyPutBackByOneInstance_IsReadByAnother_WithItsCountOnlyUnderTheSameKey(bool sameKey, int expected)
    {
        string run = Guid.NewGuid().ToString("N");
        StampKey first = StampKey.Of(32, seed: 1);
        StampKey second = sameKey ? first : StampKey.Of(32, seed: 2);
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await using (RabbitMqTransport putsBack = Transport(run, first))
            {
                await putsBack.SendAsync(new[] { new MessageEnvelope { Name = MessageName, Body = Encoding.UTF8.GetBytes("{\"Id\":1,\"Note\":\"fail\"}") } }, default);
                ReceivedMessage delivery = await ReceiveOneAsync(putsBack);
                Assert.Equal(1, delivery.DeliveryCount);
                await putsBack.NackAsync(delivery, TimeSpan.Zero, default);
            }

            // The first instance may have taken the copy before it closed, and handed it back marked as
            // redelivered; it is published again as the transport publishes a copy, so the other
            // instance reads it as a copy fresh from the queue.
            BasicGetResult copy = await TakeAsync(raw, "service-" + run);
            await raw.BasicPublishAsync(string.Empty, "service-" + run, false, new BasicProperties(copy.BasicProperties), copy.Body);

            await using RabbitMqTransport readsIt = Transport(run, second);
            ReceivedMessage read = await ReceiveOneAsync(readsIt);

            Assert.Equal(expected, read.DeliveryCount);
            await readsIt.AckAsync(read, default);
        }
    }

    /// <summary>
    /// Two instances of one service, with one key, take turns at the queue: a message whose handler
    /// always fails runs exactly the attempt limit in all, then is dead-lettered, and nothing they log
    /// holds the key.
    /// </summary>
    [RabbitMqFact]
    public async Task APoisonMessage_AcrossAlternatingInstances_IsDeadLetteredAfterExactlyMaxAttempts_AndNoLogHoldsTheKey()
    {
        string run = Guid.NewGuid().ToString("N");
        StampKey key = StampKey.Of(32, seed: 3);
        var logs = new CapturingLoggerFactory();
        await using ServiceProvider a = Instance(run, key, logs);
        await using ServiceProvider b = Instance(run, key, logs);
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            // Both consume before the message is published.
            await a.GetRequiredService<MessageConsumer>().RunOnceAsync();
            await b.GetRequiredService<MessageConsumer>().RunOnceAsync();
            var properties = new BasicProperties
            {
                MessageId = Guid.NewGuid().ToString("N"),
                Type = MessageName,
                ContentType = MessageEnvelope.JsonContentType,
                Headers = new Dictionary<string, object?>(),
            };
            await raw.BasicPublishAsync("test-" + run, MessageName, false, properties, Encoding.UTF8.GetBytes("{\"Id\":31,\"Note\":\"fail\"}"));

            BasicGetResult? deadLettered = null;
            ServiceProvider[] turns = { a, b };
            for (int round = 0; deadLettered is null && round < 4 * MaxReceiveTries; round++)
            {
                await turns[round % 2].GetRequiredService<MessageConsumer>().RunOnceAsync();
                deadLettered = await raw.BasicGetAsync("service-" + run + ".dead-letter", true);
            }

            Assert.NotNull(deadLettered);
            Assert.Equal(MaxAttempts, Ran(a) + Ran(b));
            Assert.Equal("{\"Id\":31,\"Note\":\"fail\"}", Encoding.UTF8.GetString(deadLettered!.Body.Span));
            Assert.Equal(0u, await raw.MessageCountAsync("service-" + run));
            Assert.NotEmpty(logs.Lines);
            foreach (string line in logs.Lines)
            {
                AssertKeyAbsent(key, line);
            }
        }
    }

    private static void SetKey(RabbitMqTransportOptions options, int? length)
    {
        if (length is int bytes)
        {
            options.DeliveryStampKey = StampKey.Of(bytes);
        }
    }

    private static void AssertChecked(Exception? thrown, bool accepted)
    {
        if (accepted)
        {
            Assert.Null(thrown);
        }
        else
        {
            Assert.IsAssignableFrom<ArgumentException>(thrown);
        }
    }

    private static void AssertKeyAbsent(StampKey key, string text)
    {
        foreach (string form in key.Forms)
        {
            Assert.DoesNotContain(form, text, StringComparison.Ordinal);
        }
    }

    private static int Ran(ServiceProvider instance) => instance.GetRequiredService<MessageProbe>().Count("placed:31");

    private RabbitMqTransport Transport(string run, StampKey key) => new RabbitMqTransport(
        new RabbitMqTransportOptions
        {
            ConnectionUri = _broker.ConnectionUri,
            Exchange = "test-" + run,
            ServiceName = "service-" + run,
            DeliveryStampKey = key,
        },
        new[] { MessageName });

    // One instance of the service, wired as an application wires it, with one connection of its own.
    private ServiceProvider Instance(string run, StampKey key, CapturingLoggerFactory logs)
    {
        var services = new ServiceCollection();
        services.AddSingleton<MessageProbe>();
        services.AddSingleton<ILoggerFactory>(logs);
        services.AddRogue();
        services.AddRogueMessaging(static options =>
        {
            options.SerializerContext = TestMessagesJson.Default;
            options.MaxAttempts = MaxAttempts;
            options.RetryDelay = TimeSpan.Zero;
        });
        services.AddRogueRabbitMqTransport(options =>
        {
            options.ConnectionUri = _broker.ConnectionUri;
            options.Exchange = "test-" + run;
            options.ServiceName = "service-" + run;
            options.PrefetchCount = 1;
            options.DeliveryStampKey = key;
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<ReceivedMessage> ReceiveOneAsync(RabbitMqTransport transport)
    {
        for (int i = 0; i < MaxReceiveTries; i++)
        {
            IReadOnlyList<ReceivedMessage> received = await transport.ReceiveAsync(1, default);
            if (received.Count > 0)
            {
                return Assert.Single(received);
            }
        }
        throw new Xunit.Sdk.XunitException("No delivery arrived.");
    }

    private static async Task<BasicGetResult> TakeAsync(IChannel raw, string queue)
    {
        for (int i = 0; i < MaxReceiveTries; i++)
        {
            if (await raw.BasicGetAsync(queue, true) is { } taken)
            {
                return taken;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        throw new Xunit.Sdk.XunitException("The copy never reached the queue.");
    }

    /// <summary>
    /// A key of printable ASCII, so its text and its bytes are the same secret; it converts to whichever
    /// of a string or bytes the option holds.
    /// </summary>
    private readonly struct StampKey
    {
        private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";

        private readonly string _text;

        private StampKey(string text) => _text = text;

        public byte[] Bytes => Encoding.ASCII.GetBytes(_text);

        /// <summary>Every form the key could be written in: its text, hex and Base64.</summary>
        public IEnumerable<string> Forms => new[]
        {
            _text,
            Convert.ToHexString(Bytes),
            Convert.ToHexString(Bytes).ToLowerInvariant(),
            Convert.ToBase64String(Bytes),
        }.Where(static f => f.Length > 0);

        public static StampKey Of(int bytes, int seed = 0) =>
            new StampKey(new string(Enumerable.Range(0, bytes).Select(i => Alphabet[((i * 7) + (seed * 13) + 3) % Alphabet.Length]).ToArray()));

        public static implicit operator string(StampKey key) => key._text;

        public static implicit operator byte[](StampKey key) => key.Bytes;

        public static implicit operator ReadOnlyMemory<byte>(StampKey key) => key.Bytes;
    }

    /// <summary>Keeps every line logged, with its exception, so a test can search them.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly ILogger _logger;
        private readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();

        public CapturingLoggerFactory()
        {
            // A proxy, as the other log tests build theirs, so no five-parameter Log method is written here.
            _logger = DispatchProxy.Create<ILogger, LogLineSink>();
            ((LogLineSink)_logger).Lines = _lines;
        }

        public string[] Lines => _lines.ToArray();

        public ILogger CreateLogger(string categoryName) => _logger;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A logger enabled at every level that keeps each entry's message and exception as text.</summary>
    internal class LogLineSink : DispatchProxy
    {
        public ConcurrentQueue<string>? Lines { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILogger.Log))
            {
                Lines!.Enqueue(args![2] + " " + args[3]);
            }
            return targetMethod?.Name == nameof(ILogger.IsEnabled) ? true : null;
        }
    }
}
