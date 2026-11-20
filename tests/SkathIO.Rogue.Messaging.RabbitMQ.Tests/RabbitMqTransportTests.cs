using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>What the transport refuses before it reaches a broker, and how it is registered.</summary>
public sealed class RabbitMqTransportTests
{
    // Nothing listens here; any test that reached the broker would fail to connect.
    private static readonly Uri _nowhere = new Uri("amqp://127.0.0.1:1/");

    [Fact]
    public void Capabilities_AreNativeDeadLetterAndBatchSend_WithoutNativeDelay()
    {
        var transport = new RabbitMqTransport(new RabbitMqTransportOptions { DeliveryStampKey = RabbitMqHost.DeliveryStampKey }, Array.Empty<string>());

        Assert.Equal(TransportCapabilities.NativeDeadLetter | TransportCapabilities.BatchSend, transport.Capabilities);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Receive_RefusesACountBelowOne(int maxCount)
    {
        await using var transport = new RabbitMqTransport(new RabbitMqTransportOptions { DeliveryStampKey = RabbitMqHost.DeliveryStampKey, ConnectionUri = _nowhere, ServiceName = "billing" }, Array.Empty<string>());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await transport.ReceiveAsync(maxCount, CancellationToken.None));
    }

    [Fact]
    public async Task Receive_WithoutAServiceName_IsRefused_SinceThereIsNoQueue()
    {
        await using var transport = new RabbitMqTransport(new RabbitMqTransportOptions { DeliveryStampKey = RabbitMqHost.DeliveryStampKey, ConnectionUri = _nowhere }, Array.Empty<string>());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.ReceiveAsync(1, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("orders.#")]
    [InlineData("has space")]
    public async Task Send_RefusesANameThatIsNoRoutingKey_BeforeConnecting(string name)
    {
        await using var transport = new RabbitMqTransport(new RabbitMqTransportOptions { DeliveryStampKey = RabbitMqHost.DeliveryStampKey, ConnectionUri = _nowhere }, Array.Empty<string>());

        await Assert.ThrowsAsync<ArgumentException>(async () => await transport.SendAsync(new[] { new MessageEnvelope { Name = name } }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await transport.SendAsync(new MessageEnvelope[] { null! }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await transport.SendAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Send_OfAnEmptyBatch_SendsNothing_AndDoesNotConnect()
    {
        await using var transport = new RabbitMqTransport(new RabbitMqTransportOptions { DeliveryStampKey = RabbitMqHost.DeliveryStampKey, ConnectionUri = _nowhere }, Array.Empty<string>());

        await transport.SendAsync(Array.Empty<MessageEnvelope>(), CancellationToken.None);
    }

    [Fact]
    public async Task Settling_AMessageItDidNotDeliver_IsRefused()
    {
        await using var transport = new RabbitMqTransport(new RabbitMqTransportOptions { DeliveryStampKey = RabbitMqHost.DeliveryStampKey, ConnectionUri = _nowhere }, Array.Empty<string>());
        var foreign = new ReceivedMessage(new MessageEnvelope { Name = "orders.placed" }, 1);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.AckAsync(foreign, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.NackAsync(foreign, TimeSpan.Zero, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.DeadLetterAsync(foreign, "reason", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await transport.NackAsync(foreign, TimeSpan.FromTicks(-1), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await transport.DeadLetterAsync(foreign, null!, CancellationToken.None));
    }

    [Fact]
    public async Task ADisposedTransport_RefusesToConnect()
    {
        var transport = new RabbitMqTransport(new RabbitMqTransportOptions { DeliveryStampKey = RabbitMqHost.DeliveryStampKey, ConnectionUri = _nowhere, ServiceName = "billing" }, Array.Empty<string>());
        await transport.DisposeAsync();
        await transport.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await transport.SendAsync(new[] { new MessageEnvelope { Name = "orders.placed" } }, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await transport.ReceiveAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Registration_ReplacesAnEarlierTransport_AndSubscribesToTheHandledNamesOnly()
    {
        var services = new ServiceCollection();
        services.AddSingleton<MessageProbe>();
        services.AddSingleton<IMessageTransport>(new InMemoryTransport());
        services.AddRogue();
        services.AddRogueMessaging(static o => o.SerializerContext = TestMessagesJson.Default);
        services.AddRogueRabbitMqTransport(static o =>
        {
            o.ServiceName = "billing";
            o.DeliveryStampKey = RabbitMqHost.DeliveryStampKey;
        });
        await using ServiceProvider provider = services.BuildServiceProvider();

        var transport = Assert.IsType<RabbitMqTransport>(provider.GetRequiredService<IMessageTransport>());

        Assert.Same(transport, provider.GetRequiredService<RabbitMqTransport>());
        Assert.Equal(new[] { "rmq.order-placed", "rmq.reserve" }, transport.Subscriptions.OrderBy(static n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("billing")]
    [InlineData(null)]
    public void TheTransportsServiceName_IsTheOptionsServiceName(string? serviceName)
    {
        using var transport = new RabbitMqTransport(new RabbitMqTransportOptions { ServiceName = serviceName, DeliveryStampKey = RabbitMqHost.DeliveryStampKey }, Array.Empty<string>());

        Assert.Equal(serviceName, ((IMessageTransport)transport).ServiceName);
    }

    [Fact]
    public void AProviderDisposedSynchronously_DisposesTheTransport()
    {
        var services = new ServiceCollection();
        services.AddRogueRabbitMqTransport(static o =>
        {
            o.ConnectionUri = _nowhere;
            o.DeliveryStampKey = RabbitMqHost.DeliveryStampKey;
        });
        ServiceProvider provider = services.BuildServiceProvider();
        var transport = provider.GetRequiredService<RabbitMqTransport>();

        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() => transport.SendAsync(new[] { new MessageEnvelope { Name = "orders.placed" } }, CancellationToken.None).AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public void Registration_NeedsAConfiguration()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddRogueRabbitMqTransport(null!));
        Assert.Throws<ArgumentNullException>(() => RabbitMqServiceCollectionExtensions.AddRogueRabbitMqTransport(null!, static _ => { }));
    }
}
