using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// The in-process delivery counts were replaced by the signed count stamped on each retry copy, and
/// nothing used them any more: the class and its tests are removed rather than left as dead code.
/// </summary>
public sealed class UnusedDeliveryCountsRemovedTests
{
    [Fact]
    public void TheTransportAssembly_HasNoDeliveryCountsType()
    {
        Assembly transport = typeof(RabbitMqTransportOptions).Assembly;

        Type? counts = transport.GetType("SkathIO.Rogue.Messaging.RabbitMQ.DeliveryCounts", throwOnError: false);

        Assert.Null(counts);
        Assert.DoesNotContain(transport.GetTypes(), type => type.Name == "DeliveryCounts");
    }

    [Fact]
    public void TheTestAssembly_HasNoDeliveryCountsTests()
    {
        Assembly tests = typeof(UnusedDeliveryCountsRemovedTests).Assembly;

        Assert.DoesNotContain(tests.GetTypes().Select(type => type.Name), name => name == "DeliveryCountsTests");
    }
}
