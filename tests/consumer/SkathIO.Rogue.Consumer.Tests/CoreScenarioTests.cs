using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SkathIO.Rogue.Consumer.App;
using Xunit;

namespace SkathIO.Rogue.Consumer.Tests;

/// <summary>Send, Publish and streams through the packed packages, with the app's behaviors woven in.</summary>
public sealed class CoreScenarioTests
{
    [Fact]
    public async Task Send_ACommand_RunsItsHandlerInsideTheBehavior_AndPublishesToEveryEventHandler()
    {
        await using var host = new ConsumerHost();

        int number = await host.Mediator.Send(new PlaceOrder { Customer = "Ada", Quantity = 2 });

        Assert.Equal(1, number);
        Assert.Equal(new[] { "before PlaceOrder", "handled PlaceOrder 1" }, host.Log.Entries().Take(2));
        Assert.Equal("after PlaceOrder", host.Log.Entries()[host.Log.Entries().Count - 1]);
        Assert.Contains("receipt for 1", host.Log.Entries());
        Assert.Contains("stock for 1", host.Log.Entries());
    }

    [Fact]
    public async Task Send_ACommandWithNoResponse_AndAQuery_ReachTheirHandlers()
    {
        await using var host = new ConsumerHost();
        await host.Mediator.Send(new PlaceOrder { Customer = "Ada", Quantity = 1 });

        int before = await host.Mediator.Send(new CountOrders());
        await host.Mediator.Send(new CancelOrders());
        int after = await host.Mediator.Send(new CountOrders());

        Assert.Equal(1, before);
        Assert.Equal(0, after);
        Assert.Contains("before CancelOrders", host.Log.Entries());
        Assert.Contains("before CountOrders", host.Log.Entries());
    }

    [Fact]
    public async Task Publish_AnEvent_ReachesBothHandlers()
    {
        await using var host = new ConsumerHost();

        await host.Mediator.Publish(new OrderPlaced(7));

        Assert.Equal(new[] { "receipt for 7", "stock for 7" }, host.Log.Entries().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CreateStream_RunsTheHandler_ThroughTheStreamBehavior()
    {
        await using var host = new ConsumerHost();
        var items = new List<int>();

        await foreach (int item in host.Mediator.CreateStream(new CountTo(3)))
        {
            items.Add(item);
        }

        Assert.Equal(new[] { 10, 20, 30 }, items);
        Assert.Contains("stream CountTo", host.Log.Entries());
    }
}
