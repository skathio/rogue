using System;
using System.Collections.Generic;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>How deliveries are read: built in memory, as the client hands them over, so no broker is needed.</summary>
public sealed class RabbitMqWireTests
{
    private static readonly RabbitMqTransportOptions _options = new RabbitMqTransportOptions { Exchange = "shop", ServiceName = "billing" }
        .MapRoutingKey("legacy", "legacy.order.placed", "orders.placed")
        .MapHeader("event-type", "StockReserved", "stock.reserved")
        .Bind("legacy", "legacy.#");

    [Fact]
    public void AnEnvelope_RoundTripsThroughTheProperties()
    {
        var sent = new MessageEnvelope
        {
            Name = "orders.placed",
            CorrelationId = "c-1",
            CausationId = "m-0",
            TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            Body = Encoding.UTF8.GetBytes("{}"),
        };

        BasicProperties properties = RabbitMqWire.ToProperties(sent);
        MessageEnvelope received = RabbitMqWire.ToEnvelope(_options, Delivery("shop", "orders.placed", AsReceived(properties), sent.Body));

        Assert.Equal(DeliveryModes.Persistent, properties.DeliveryMode);
        Assert.Equal(sent.Id, received.Id);
        Assert.Equal("orders.placed", received.Name);
        Assert.Equal(MessageEnvelope.JsonContentType, received.ContentType);
        Assert.Equal("c-1", received.CorrelationId);
        Assert.Equal("m-0", received.CausationId);
        Assert.Equal(sent.TraceParent, received.TraceParent);
        Assert.Equal("{}", Encoding.UTF8.GetString(received.Body.Span));
        Assert.False(properties.Headers!.ContainsKey(RabbitMqWire.DeliveryCountHeader));
    }

    [Fact]
    public void AForeignMessage_WithoutIdOrContentType_GetsAFreshIdAndNoContentType()
    {
        MessageEnvelope received = RabbitMqWire.ToEnvelope(_options, Delivery("legacy", "legacy.order.placed", new BasicProperties(), Encoding.UTF8.GetBytes("{}")));

        Assert.Equal("orders.placed", received.Name);
        Assert.Equal(32, received.Id.Length);
        Assert.Equal(string.Empty, received.ContentType);
        Assert.Null(received.CausationId);
    }

    [Fact]
    public void TheBody_IsCopied_SinceTheClientReusesItsBuffer()
    {
        byte[] buffer = Encoding.UTF8.GetBytes("{}");
        MessageEnvelope received = RabbitMqWire.ToEnvelope(_options, Delivery("shop", "orders.placed", new BasicProperties(), buffer));

        buffer[0] = (byte)'x';

        Assert.Equal("{}", Encoding.UTF8.GetString(received.Body.Span));
    }

    [Theory]
    [InlineData("shop", "orders.placed", "orders.placed", "orders.placed")]
    [InlineData("shop", "orders.placed", null, "orders.placed")]
    [InlineData("legacy", "legacy.order.placed", null, "orders.placed")]
    [InlineData("legacy", "legacy.order.placed", "Contoso.OrderPlaced", "orders.placed")]
    [InlineData("legacy", "LEGACY.ORDER.PLACED", null, "")]
    [InlineData("legacy", "legacy.events", null, "")]
    [InlineData("", "billing", "orders.placed", "orders.placed")]
    [InlineData("other", "orders.placed", null, "")]
    [InlineData("legacy", "legacy.events", "orders.placed", "")]
    [InlineData("other", "orders.placed", "orders.placed", "")]
    public void WithoutAHeaderMatch_TheNameComesFromARoutingKeyMap_ThenTheTypeProperty_ThenTheOwnExchangesRoutingKey(string exchange, string routingKey, string? type, string expected)
    {
        Assert.Equal(expected, RabbitMqWire.ResolveName(_options, exchange, routingKey, new BasicProperties { Type = type }));
    }

    [Theory]
    [InlineData("StockReserved", "legacy.order.placed", null, "stock.reserved")]
    [InlineData("StockReserved", "legacy.events", "Contoso.StockReserved", "stock.reserved")]
    [InlineData("Unknown", "legacy.order.placed", null, "orders.placed")]
    [InlineData("Unknown", "legacy.events", "orders.placed", "")]
    [InlineData("Unknown", "legacy.events", null, "")]
    public void AHeaderMatch_NamesTheMessage_BeforeARoutingKeyMapOrTheTypeProperty(string eventType, string routingKey, string? type, string expected)
    {
        var properties = new BasicProperties
        {
            Type = type,
            Headers = new Dictionary<string, object?> { ["event-type"] = Encoding.UTF8.GetBytes(eventType) },
        };

        Assert.Equal(expected, RabbitMqWire.ResolveName(_options, "legacy", routingKey, properties));
    }

    public static TheoryData<object?, bool, int> Counts => new TheoryData<object?, bool, int>
    {
        { null, false, 1 },
        { null, true, 2 },
        { 3, false, 3 },
        { 3, true, 4 },
        { 3L, false, 3 },
        { (short)2, false, 2 },
        { (byte)2, false, 2 },
        { 0, false, 1 },
        { -5, false, 1 },
        { long.MaxValue, false, RabbitMqWire.MaxDeliveryCount },
        { RabbitMqWire.MaxDeliveryCount, true, RabbitMqWire.MaxDeliveryCount + 1 },
        { Encoding.UTF8.GetBytes("3"), false, 1 },
        { 2.5, false, 1 },
    };

    [Theory]
    [MemberData(nameof(Counts))]
    public void TheDeliveryCount_ComesFromTheTransportsHeader_PlusOneWhenRedelivered(object? header, bool redelivered, int expected)
    {
        var properties = new BasicProperties();
        if (header is not null)
        {
            properties.Headers = new Dictionary<string, object?> { [RabbitMqWire.DeliveryCountHeader] = header };
        }

        Assert.Equal(expected, RabbitMqWire.DeliveryCount(properties, redelivered));
    }

    [Fact]
    public void AStampedCount_IsReadBack()
    {
        BasicProperties properties = RabbitMqWire.ToProperties(new MessageEnvelope { Name = "orders.placed" }, 4, "because");

        Assert.Equal(4, RabbitMqWire.DeliveryCount(properties, false));
        Assert.Equal("because", RabbitMqWire.HeaderString(properties, RabbitMqWire.DeadLetterReasonHeader));
    }

    [Fact]
    public void TheOrigin_RoundTripsThroughItsHeader_AndSurvivesARetryOrDeadLetterCopy()
    {
        var sent = new MessageEnvelope { Name = "orders.placed", Origin = "billing", Body = Encoding.UTF8.GetBytes("{}") };

        MessageEnvelope received = RabbitMqWire.ToEnvelope(_options, Delivery("shop", "orders.placed", AsReceived(RabbitMqWire.ToProperties(sent)), sent.Body));
        MessageEnvelope copy = RabbitMqWire.ToEnvelope(_options, Delivery(string.Empty, "billing", AsReceived(RabbitMqWire.ToProperties(received, 2, "because")), sent.Body));

        Assert.Equal("billing", received.Origin);
        Assert.Equal("billing", copy.Origin);
    }

    [Fact]
    public void AMessageWithoutAnOrigin_SendsNoOriginHeader_AndIsReadWithNone()
    {
        BasicProperties properties = RabbitMqWire.ToProperties(new MessageEnvelope { Name = "orders.placed" });

        Assert.False(properties.Headers!.ContainsKey(RabbitMqWire.OriginHeader));
        Assert.Null(RabbitMqWire.ToEnvelope(_options, Delivery("shop", "orders.placed", properties, Array.Empty<byte>())).Origin);
    }

    [Fact]
    public void AnOriginOverTheHeaderLimit_IsReadAsNone()
    {
        var properties = new BasicProperties
        {
            Headers = new Dictionary<string, object?> { [RabbitMqWire.OriginHeader] = Encoding.UTF8.GetBytes(new string('a', RabbitMqWire.MaxHeaderBytes + 1)) },
        };

        Assert.Null(RabbitMqWire.ToEnvelope(_options, Delivery("shop", "orders.placed", properties, Array.Empty<byte>())).Origin);
    }

    [Fact]
    public void HeaderStrings_OverTheLimitOrNotUtf8_AreIgnored()
    {
        var properties = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                ["at"] = Encoding.UTF8.GetBytes(new string('a', RabbitMqWire.MaxHeaderBytes)),
                ["over"] = Encoding.UTF8.GetBytes(new string('a', RabbitMqWire.MaxHeaderBytes + 1)),
                ["text-over"] = new string('a', RabbitMqWire.MaxHeaderBytes + 1),
                ["bad"] = new byte[] { 0xFF, 0xFE },
                ["number"] = 7,
            },
        };

        Assert.Equal(RabbitMqWire.MaxHeaderBytes, RabbitMqWire.HeaderString(properties, "at")!.Length);
        Assert.Null(RabbitMqWire.HeaderString(properties, "over"));
        Assert.Null(RabbitMqWire.HeaderString(properties, "text-over"));
        Assert.Null(RabbitMqWire.HeaderString(properties, "bad"));
        Assert.Null(RabbitMqWire.HeaderString(properties, "number"));
        Assert.Null(RabbitMqWire.HeaderString(properties, "missing"));
        Assert.Null(RabbitMqWire.HeaderString(new BasicProperties(), "missing"));
    }

    private static BasicDeliverEventArgs Delivery(string exchange, string routingKey, IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body) =>
        new BasicDeliverEventArgs("consumer", 1, false, exchange, routingKey, properties, body);

    // Headers set in this process stay strings; the broker hands string headers back as bytes.
    private static BasicProperties AsReceived(BasicProperties sent)
    {
        var headers = new Dictionary<string, object?>();
        foreach (KeyValuePair<string, object?> header in sent.Headers!)
        {
            headers[header.Key] = header.Value is string text ? Encoding.UTF8.GetBytes(text) : header.Value;
        }
        return new BasicProperties(sent) { Headers = headers };
    }
}
