using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

/// <summary>
/// A service that both publishes and consumes the same marked event runs its local handlers once, when
/// it publishes, and not again when its own copy comes back from the bus. Other services still get the
/// event, and commands are not affected.
/// </summary>
public sealed class OwnOriginEventTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task EventPublishedThroughThePublisher_RunsItsLocalHandlersOnce_WhenItsOwnCopyArrives(int events)
    {
        await using MessagingHost host = MessagingHost.Create();

        for (int id = 1; id <= events; id++)
        {
            await PublishLocallyAsync(host, new OrderPlaced(id, "none"));
        }
        string[] expected = Enumerable.Range(1, events).Select(static id => "placed:" + id).ToArray();
        // The local handlers ran at once, before the consumer took anything from the bus.
        Assert.Equal(expected, host.Probe.Ran);

        await host.RunRoundsAsync(3);

        Assert.Equal(expected, host.Probe.Ran);
        Assert.Empty(host.DeadLettered);
        Assert.Empty(await host.DrainAsync());
    }

    [Fact]
    public async Task EventPublishedThroughThePublisher_IsStillSentToTheBus()
    {
        await using MessagingHost host = MessagingHost.Create();

        await PublishLocallyAsync(host, new OrderPlaced(7, "none"));
        MessageEnvelope sent = Assert.Single(await host.DrainAsync());

        Assert.Equal("test.order-placed", sent.Name);
        Assert.Equal(new[] { "placed:7" }, host.Probe.Ran);
    }

    [Fact]
    public async Task OwnCopyOfAnEvent_DeliveredBackToThePublishingService_IsDropped()
    {
        await using MessagingHost host = MessagingHost.Create();
        await PublishLocallyAsync(host, new OrderPlaced(7, "none"));
        MessageEnvelope ownCopy = Assert.Single(await host.DrainAsync());

        await host.DeliverAsync(ownCopy);

        Assert.Equal(new[] { "placed:7" }, host.Probe.Ran);
        Assert.Empty(host.DeadLettered);
        Assert.Empty(await host.DrainAsync());
    }

    [Fact]
    public async Task EventFromAnotherService_RunsTheLocalHandlers()
    {
        await using MessagingHost host = MessagingHost.Create();

        await host.DeliverAsync(MessagingHost.Envelope("test.order-placed", "{\"Id\":9,\"Note\":\"none\"}"));

        Assert.Equal(new[] { "placed:9" }, host.Probe.Ran);
    }

    [Fact]
    public async Task CommandSentByTheServiceThatHandlesIt_IsStillHandled()
    {
        await using MessagingHost host = MessagingHost.Create();

        await host.Services.GetRequiredService<IMessageBus>().Publish(new ReserveStock(5));
        await host.RunRoundsAsync(1);

        Assert.Equal(new[] { "reserve:5" }, host.Probe.Ran);
        Assert.Empty(host.DeadLettered);
    }

    [Fact]
    public async Task OwnCommandCopy_DeliveredBack_IsHandledEachTime()
    {
        await using MessagingHost host = MessagingHost.Create();
        await host.Services.GetRequiredService<IMessageBus>().Publish(new ReserveStock(5));
        MessageEnvelope sent = Assert.Single(await host.DrainAsync());

        await host.DeliverAsync(sent);

        Assert.Equal(new[] { "reserve:5" }, host.Probe.Ran);
    }

    private static async Task PublishLocallyAsync(MessagingHost host, IEvent @event)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(@event);
    }
}
