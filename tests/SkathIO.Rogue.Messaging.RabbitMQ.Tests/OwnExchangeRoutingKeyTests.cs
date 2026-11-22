using System;
using System.Collections.Generic;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// On the service's own exchange the routing key is what the queue was bound by, so it always names the
/// delivery: a header map, which reads a header the sender controls, must never rename it.
/// </summary>
public sealed class OwnExchangeRoutingKeyTests
{
    private const string OwnExchange = "shop";

    [Fact]
    public void WithNoHeaderMap_TheOwnExchangesRoutingKey_NamesTheDelivery()
    {
        RabbitMqTransportOptions options = Options();

        Assert.Equal("orders.placed", RabbitMqWire.ResolveName(options, OwnExchange, "orders.placed", Headers(("event-type", "StockReserved"))));
    }

    [Fact]
    public void OneMatchingHeaderMap_DoesNotRenameAnOwnExchangeDelivery()
    {
        RabbitMqTransportOptions options = Options().MapHeader("event-type", "StockReserved", "stock.reserved");

        Assert.Equal("orders.placed", RabbitMqWire.ResolveName(options, OwnExchange, "orders.placed", Headers(("event-type", "StockReserved"))));
    }

    [Fact]
    public void EveryMatchingHeaderMap_LeavesTheOwnExchangesRoutingKeyAsTheName()
    {
        RabbitMqTransportOptions options = Options()
            .MapHeader("event-type", "StockReserved", "stock.reserved")
            .MapHeader("kind", "Refund", "payments.refunded")
            .MapHeader("event-type", "OrderShipped", "orders.shipped");

        BasicProperties properties = Headers(("event-type", "StockReserved"), ("kind", "Refund"));

        Assert.Equal("orders.placed", RabbitMqWire.ResolveName(options, OwnExchange, "orders.placed", properties));
    }

    [Fact]
    public void AMatchingHeaderMap_AndATypeProperty_TogetherDoNotRenameAnOwnExchangeDelivery()
    {
        RabbitMqTransportOptions options = Options().MapHeader("event-type", "StockReserved", "stock.reserved");
        BasicProperties properties = Headers(("event-type", "StockReserved"));
        properties.Type = "stock.reserved";

        Assert.Equal("orders.placed", RabbitMqWire.ResolveName(options, OwnExchange, "orders.placed", properties));
    }

    [Fact]
    public void AnEnvelopeReadFromTheOwnExchange_IsNamedByItsRoutingKey_EvenWhenAHeaderMapMatches()
    {
        RabbitMqTransportOptions options = Options().MapHeader("event-type", "StockReserved", "stock.reserved");
        var delivery = new BasicDeliverEventArgs(
            "consumer", 1, false, OwnExchange, "orders.placed", Headers(("event-type", "StockReserved")), Encoding.UTF8.GetBytes("{}"));

        MessageEnvelope received = RabbitMqWire.ToEnvelope(options, delivery);

        Assert.Equal("orders.placed", received.Name);
    }

    [Fact]
    public void OnAnotherExchange_AMatchingHeaderMap_StillNamesTheDelivery()
    {
        RabbitMqTransportOptions options = Options().MapHeader("event-type", "StockReserved", "stock.reserved");

        Assert.Equal("stock.reserved", RabbitMqWire.ResolveName(options, "legacy", "legacy.events", Headers(("event-type", "StockReserved"))));
    }

    private static RabbitMqTransportOptions Options() =>
        new RabbitMqTransportOptions { Exchange = OwnExchange, ServiceName = "billing" }.Bind("legacy", "legacy.#");

    // The broker hands string headers back as bytes, so they are built that way here.
    private static BasicProperties Headers(params (string Name, string Value)[] headers)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string name, string value) in headers)
        {
            map[name] = Encoding.UTF8.GetBytes(value);
        }
        return new BasicProperties { Headers = map };
    }
}
