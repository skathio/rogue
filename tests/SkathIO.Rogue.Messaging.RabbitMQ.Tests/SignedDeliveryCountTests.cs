using System;
using System.Collections.Generic;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// A delivery's count comes only from a stamp the transport signed on the copy it sent back to its own
/// queue: the count travels with the copy, so no number of other messages in retry can make the
/// transport forget it, and a sender, which cannot sign, never gets a count above 1 for its message,
/// whatever id it reuses or header it forges. Deliveries are built in memory, so no broker is needed.
/// </summary>
public sealed class SignedDeliveryCountTests
{
    private const string Queue = "billing";
    private static readonly RabbitMqTransportOptions _options = new RabbitMqTransportOptions { Exchange = "shop", ServiceName = Queue };

    public static TheoryData<int, bool, int> SignedCounts => new TheoryData<int, bool, int>
    {
        { 1, false, 1 },
        { 2, false, 2 },
        { 2, true, 3 },
        { 3, false, 3 },
        { RabbitMqWire.MaxDeliveryCount, false, RabbitMqWire.MaxDeliveryCount },
        { RabbitMqWire.MaxDeliveryCount, true, RabbitMqWire.MaxDeliveryCount + 1 },
        { RabbitMqWire.MaxDeliveryCount + 1, false, RabbitMqWire.MaxDeliveryCount },
        { 0, false, 1 },
    };

    [Theory]
    [MemberData(nameof(SignedCounts))]
    public void ACopyTheTransportSigned_HasItsSignedCount_HeldToItsRange_PlusOneWhenRedelivered(int signed, bool redelivered, int expected)
    {
        var stamp = Stamp();
        MessageEnvelope sent = Envelope("m-1");

        BasicDeliverEventArgs copy = Copy(sent, stamp.Sign(sent.Name, sent.Id, signed), redelivered);

        Assert.Equal(expected, stamp.CountOf(copy, Received(copy)));
    }

    [Fact]
    public void ACopysCount_IsKept_HoweverManyOtherMessagesWereSentBackSinceThen()
    {
        var stamp = Stamp();
        MessageEnvelope sent = Envelope("m-0");
        string signed = stamp.Sign(sent.Name, sent.Id, 3);
        for (int i = 1; i <= 25_000; i++)
        {
            stamp.Sign(sent.Name, "m-" + i, 2);
        }

        BasicDeliverEventArgs copy = Copy(sent, signed);

        Assert.Equal(3, stamp.CountOf(copy, Received(copy)));
    }

    [Fact]
    public void AFreshDelivery_ReusingTheIdOfAMessageInRetry_StartsAt1()
    {
        var stamp = Stamp();
        MessageEnvelope sent = Envelope("m-1");
        stamp.Sign(sent.Name, sent.Id, 3);

        BasicDeliverEventArgs viaExchange = Delivery("shop", sent.Name, RabbitMqWire.ToProperties(sent), false);
        BasicDeliverEventArgs viaQueue = Copy(sent, stamp: null);

        Assert.Equal(1, stamp.CountOf(viaExchange, Received(viaExchange)));
        Assert.Equal(1, stamp.CountOf(viaQueue, Received(viaQueue)));
    }

    [Fact]
    public void AStampCopiedOntoAnotherMessage_OrWithItsCountEdited_StartsAt1()
    {
        var stamp = Stamp();
        MessageEnvelope sent = Envelope("m-1");
        string signed = stamp.Sign(sent.Name, sent.Id, 3);
        string mac = signed.Substring(signed.IndexOf('.', StringComparison.Ordinal) + 1);

        MessageEnvelope otherId = Envelope("m-2");
        MessageEnvelope otherName = Envelope("m-1");
        otherName.Name = "orders.shipped";

        Assert.Equal(1, CountOf(stamp, otherId, signed));
        Assert.Equal(1, CountOf(stamp, otherName, signed));
        Assert.Equal(1, CountOf(stamp, sent, "2." + mac));
        Assert.Equal(1, CountOf(stamp, sent, "4." + mac));
        Assert.Equal(3, CountOf(stamp, sent, signed));
    }

    [Fact]
    public void AStampSignedUnderAnotherKey_StartsAt1()
    {
        DeliveryStamp mine = Stamp(RabbitMqHost.DeliveryStampKey);
        DeliveryStamp theirs = Stamp("another-delivery-stamp-key-of-32");
        MessageEnvelope sent = Envelope("m-1");

        Assert.Equal(1, CountOf(mine, sent, theirs.Sign(sent.Name, sent.Id, 3)));
    }

    [Fact]
    public void AStampSignedByAnotherInstance_UnderTheSameKey_KeepsItsCount()
    {
        DeliveryStamp mine = Stamp(RabbitMqHost.DeliveryStampKey);
        DeliveryStamp theirs = Stamp(RabbitMqHost.DeliveryStampKey);
        MessageEnvelope sent = Envelope("m-1");

        Assert.Equal(3, CountOf(mine, sent, theirs.Sign(sent.Name, sent.Id, 3)));
    }

    [Fact]
    public void AValidStamp_OnADeliveryThroughAnExchange_StartsAt1()
    {
        var stamp = Stamp();
        MessageEnvelope sent = Envelope("m-1");
        BasicProperties properties = RabbitMqWire.ToProperties(sent, 3, deliveryStamp: stamp.Sign(sent.Name, sent.Id, 3));

        BasicDeliverEventArgs delivery = Delivery("shop", sent.Name, AsReceived(properties), false);

        Assert.Equal(1, stamp.CountOf(delivery, Received(delivery)));
    }

    public static TheoryData<string> Malformed => new TheoryData<string>
    {
        string.Empty,
        "3",
        "3.",
        ".00",
        "-3.00",
        "+3.00",
        " 3.00",
        "99999999999.00",
        "3.zz",
        "3.0",
        new string('7', RabbitMqWire.MaxHeaderBytes + 1),
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void AMalformedStamp_StartsAt1(string forged)
    {
        Assert.Equal(1, CountOf(Stamp(), Envelope("m-1"), forged));
    }

    [Fact]
    public void AForgedCountHeader_WithoutAStamp_StartsAt1()
    {
        var stamp = Stamp();
        MessageEnvelope sent = Envelope("m-1");
        BasicProperties properties = RabbitMqWire.ToProperties(sent, 1);
        properties.Headers![RabbitMqWire.DeliveryCountHeader] = 3;

        BasicDeliverEventArgs copy = Delivery(string.Empty, Queue, AsReceived(properties), false);

        Assert.Equal(1, stamp.CountOf(copy, Received(copy)));
    }

    [Fact]
    public void ACopy_KeepsItsStamp_InTheHeaderTheTransportReads()
    {
        var stamp = Stamp();
        MessageEnvelope sent = Envelope("m-1");
        string signed = stamp.Sign(sent.Name, sent.Id, 2);

        BasicProperties properties = RabbitMqWire.ToProperties(sent, 2, deliveryStamp: signed);

        Assert.Equal(signed, RabbitMqWire.HeaderString(AsReceived(properties), RabbitMqWire.DeliveryStampHeader));
        Assert.Equal(2, Assert.IsType<int>(properties.Headers![RabbitMqWire.DeliveryCountHeader]));
    }

    private static DeliveryStamp Stamp(string key = RabbitMqHost.DeliveryStampKey) => new DeliveryStamp(key);

    private static int CountOf(DeliveryStamp stamp, MessageEnvelope sent, string forged)
    {
        BasicDeliverEventArgs copy = Copy(sent, forged);
        return stamp.CountOf(copy, Received(copy));
    }

    private static MessageEnvelope Envelope(string id) => new MessageEnvelope
    {
        Id = id,
        Name = "orders.placed",
        Body = Encoding.UTF8.GetBytes("{}"),
    };

    private static MessageEnvelope Received(BasicDeliverEventArgs delivery) => RabbitMqWire.ToEnvelope(_options, delivery);

    // A copy put back on the service's queue through the default exchange, as the transport sends one.
    private static BasicDeliverEventArgs Copy(MessageEnvelope sent, string? stamp, bool redelivered = false)
    {
        BasicProperties properties = RabbitMqWire.ToProperties(sent, 1, deliveryStamp: stamp);
        return Delivery(string.Empty, Queue, AsReceived(properties), redelivered);
    }

    private static BasicDeliverEventArgs Delivery(string exchange, string routingKey, IReadOnlyBasicProperties properties, bool redelivered) =>
        new BasicDeliverEventArgs("consumer", 1, redelivered, exchange, routingKey, properties, Encoding.UTF8.GetBytes("{}"));

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
