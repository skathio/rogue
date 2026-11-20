using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>What a sender on the broker cannot steer: the name a delivery gets, and what an oversized body costs.</summary>
public sealed class RabbitMqHardeningTests
{
    private static readonly RabbitMqTransportOptions _options = new RabbitMqTransportOptions { Exchange = "shop", ServiceName = "billing", DeliveryStampKey = RabbitMqHost.DeliveryStampKey };

    [Theory]
    [InlineData("payments.refunded")]
    [InlineData("orders.placed")]
    [InlineData(null)]
    public void OnTheOwnExchange_TheRoutingKeyNamesTheMessage_WhateverTheTypeProperty(string? type)
    {
        Assert.Equal("orders.placed", RabbitMqWire.ResolveName(_options, "shop", "orders.placed", new BasicProperties { Type = type }));
    }

    [Fact]
    public void OnTheDefaultExchange_TheTypePropertyNamesTheRetriedCopy()
    {
        Assert.Equal("orders.placed", RabbitMqWire.ResolveName(_options, string.Empty, "billing", new BasicProperties { Type = "orders.placed" }));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(4 * 1024 * 1024)]
    public void Registration_GivesTheTransportTheMessagingSizeLimit(int limit)
    {
        var services = new ServiceCollection();
        services.AddRogue();
        services.AddRogueMessaging(o =>
        {
            o.SerializerContext = TestMessagesJson.Default;
            o.MaxMessageBytes = limit;
        });
        services.AddRogueRabbitMqTransport(static o =>
        {
            o.Exchange = "shop";
            o.DeliveryStampKey = RabbitMqHost.DeliveryStampKey;
        });
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(limit, provider.GetRequiredService<RabbitMqTransport>().MaxMessageBytes);
    }

    [Fact]
    public void ATransportBuiltDirectly_SetsNoLimitOfItsOwn_SoTheConsumersLimitStillDecides()
    {
        using var transport = new RabbitMqTransport(_options, Array.Empty<string>());

        Assert.Equal(int.MaxValue, transport.MaxMessageBytes);
    }
}

/// <summary>An oversized delivery against a real broker; skipped without Docker.</summary>
[Collection(RabbitMqCollection.Name)]
public sealed class RabbitMqOversizedDeliveryTests
{
    private readonly RabbitMqFixture _broker;

    public RabbitMqOversizedDeliveryTests(RabbitMqFixture broker) => _broker = broker;

    [RabbitMqFact]
    public async Task ABodyOverTheLimit_IsNackedToTheDeadLetterQueue_WithoutReachingTheConsumer()
    {
        await using RabbitMqHost host = RabbitMqHost.Create(_broker.ConnectionUri);
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await host.Consumer.RunOnceAsync();
            int limit = host.Services.GetRequiredService<MessagingOptions>().MaxMessageBytes;
            byte[] body = Encoding.UTF8.GetBytes("{\"Id\":1,\"Note\":\"" + new string('x', limit) + "\"}");
            await raw.BasicPublishAsync(host.Options.Exchange, "rmq.order-placed", body);

            BasicGetResult? deadLettered = null;
            await host.RunUntilAsync(() => deadLettered is not null, async () => deadLettered = await raw.BasicGetAsync(host.Options.DeadLetterQueue!, true));

            // Dead-lettered by the broker (x-death), not republished by the consumer with a reason.
            Assert.Empty(host.Probe.Ran);
            Assert.True(deadLettered!.BasicProperties.Headers!.ContainsKey("x-death"));
            Assert.False(deadLettered.BasicProperties.Headers.ContainsKey(RabbitMqWire.DeadLetterReasonHeader));
            Assert.Equal(body.Length, deadLettered.Body.Length);
        }
    }
}
