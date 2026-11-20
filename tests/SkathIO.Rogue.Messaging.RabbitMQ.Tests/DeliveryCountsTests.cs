using System;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// The delivery counts the transport keeps for the copies it sends back: a delivery's count comes only
/// from them and the broker's redelivered flag, at the ends of the count range and of the memory bound.
/// </summary>
public sealed class DeliveryCountsTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void AMessageNeverSentBack_IsOnItsFirstDelivery_PlusOneWhenRedelivered(bool redelivered, int expected)
    {
        var counts = new DeliveryCounts();

        Assert.Equal(expected, counts.Of(("orders.placed", "m1"), redelivered));
    }

    public static TheoryData<int, bool, int> Recorded => new TheoryData<int, bool, int>
    {
        { 2, false, 2 },
        { 2, true, 3 },
        { 0, false, 1 },
        { int.MinValue, false, 1 },
        { RabbitMqWire.MaxDeliveryCount - 1, false, RabbitMqWire.MaxDeliveryCount - 1 },
        { RabbitMqWire.MaxDeliveryCount, false, RabbitMqWire.MaxDeliveryCount },
        { RabbitMqWire.MaxDeliveryCount, true, RabbitMqWire.MaxDeliveryCount + 1 },
        { RabbitMqWire.MaxDeliveryCount + 1, false, RabbitMqWire.MaxDeliveryCount },
        { int.MaxValue, true, RabbitMqWire.MaxDeliveryCount + 1 },
    };

    [Theory]
    [MemberData(nameof(Recorded))]
    public void ARecordedCount_IsTheCount_HeldToItsRange_PlusOneWhenRedelivered(int recorded, bool redelivered, int expected)
    {
        var counts = new DeliveryCounts();
        counts.Record(("orders.placed", "m1"), recorded);

        Assert.Equal(expected, counts.Of(("orders.placed", "m1"), redelivered));
    }

    [Fact]
    public void ACount_BelongsToOneNameAndId()
    {
        var counts = new DeliveryCounts();
        counts.Record(("orders.placed", "m1"), 3);

        Assert.Equal(1, counts.Of(("orders.shipped", "m1"), false));
        Assert.Equal(1, counts.Of(("orders.placed", "m2"), false));
        Assert.Equal(3, counts.Of(("orders.placed", "m1"), false));
    }

    [Fact]
    public void Record_ReturnsTheCountItReplaces_AndRestorePutsItBack()
    {
        var counts = new DeliveryCounts();

        Assert.Null(counts.Record(("orders.placed", "m1"), 2));
        Assert.Equal(2, counts.Record(("orders.placed", "m1"), 3));
        counts.Restore(("orders.placed", "m1"), 2);
        Assert.Equal(2, counts.Of(("orders.placed", "m1"), false));

        counts.Restore(("orders.placed", "m1"), null);
        Assert.Equal(1, counts.Of(("orders.placed", "m1"), false));
    }

    [Fact]
    public void AForgottenCount_IsGone()
    {
        var counts = new DeliveryCounts();
        counts.Record(("orders.placed", "m1"), 3);

        counts.Forget(("orders.placed", "m1"));

        Assert.Equal(1, counts.Of(("orders.placed", "m1"), false));
    }

    [Fact]
    public void TwoFullGenerations_AreKept_AndTheOldestCountIsDroppedOnlyPastThem()
    {
        const int size = 3;
        var counts = new DeliveryCounts(size);
        for (int i = 0; i < 2 * size; i++)
        {
            counts.Record(("orders.placed", "m" + i), 2);
        }
        Assert.All(Ids(2 * size), id => Assert.Equal(2, counts.Of(("orders.placed", id), false)));

        counts.Record(("orders.placed", "m" + (2 * size)), 2);

        Assert.All(Ids(size), id => Assert.Equal(1, counts.Of(("orders.placed", id), false)));
        for (int i = size; i <= 2 * size; i++)
        {
            Assert.Equal(2, counts.Of(("orders.placed", "m" + i), false));
        }
    }

    [Fact]
    public void RecordingAgain_AMessageInAFullGeneration_DropsNothing()
    {
        var counts = new DeliveryCounts(2);
        counts.Record(("orders.placed", "m0"), 2);
        counts.Record(("orders.placed", "m1"), 2);
        counts.Record(("orders.placed", "m2"), 2);
        counts.Record(("orders.placed", "m3"), 2);

        counts.Record(("orders.placed", "m3"), 3);

        Assert.Equal(2, counts.Of(("orders.placed", "m0"), false));
        Assert.Equal(3, counts.Of(("orders.placed", "m3"), false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AGenerationSize_BelowOne_IsRefused(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryCounts(size));
    }

    private static string[] Ids(int count)
    {
        var ids = new string[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = "m" + i;
        }
        return ids;
    }
}
