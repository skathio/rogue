using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Messaging.Tests;

public sealed class MessageBusTests
{
    [Fact]
    public async Task EventPublishedByAnInboundHandler_IsForwarded_WithTheInboundIdsAsCausationAndCorrelation()
    {
        await using MessagingHost host = MessagingHost.Create();
        MessageEnvelope inbound = MessagingHost.Envelope("test.order-placed", "{\"Id\":7,\"Note\":\"follow-up\"}");
        inbound.Id = "in-1";
        inbound.CorrelationId = "conversation-1";

        await host.DeliverAsync(inbound);
        MessageEnvelope[] sent = await host.DrainAsync();

        // The inbound event itself is not sent back; only the event its handler published is.
        MessageEnvelope audited = Assert.Single(sent);
        Assert.Equal("test.order-audited", audited.Name);
        Assert.Equal("in-1", audited.CausationId);
        Assert.Equal("conversation-1", audited.CorrelationId);
        Assert.Equal(new[] { "placed:7" }, host.Probe.Ran);
    }

    [Fact]
    public async Task InboundMessageWithoutACorrelationId_StartsTheConversation()
    {
        await using MessagingHost host = MessagingHost.Create();
        MessageEnvelope inbound = MessagingHost.Envelope("test.order-placed", "{\"Id\":7,\"Note\":\"follow-up\"}");
        inbound.Id = "in-1";

        await host.DeliverAsync(inbound);

        Assert.Equal("in-1", Assert.Single(await host.DrainAsync()).CorrelationId);
    }

    [Fact]
    public async Task PublishOutsideAHandler_CorrelatesToItself_WithNoCause()
    {
        await using MessagingHost host = MessagingHost.Create();

        await host.Services.GetRequiredService<IMessageBus>().Publish(new StockReport(3));
        MessageEnvelope sent = Assert.Single(await host.DrainAsync());

        Assert.Equal(sent.Id, sent.CorrelationId);
        Assert.Null(sent.CausationId);
        Assert.Equal(MessageEnvelope.JsonContentType, sent.ContentType);
        Assert.Equal(new StockReport(3), JsonSerializer.Deserialize(sent.Body.Span, TestMessagesJson.Default.StockReport));
    }

    [Theory]
    [InlineData(13, false)]
    [InlineData(12, true)]
    public async Task PublishOverMaxMessageBytes_IsRefused_AndSendsNothing(int limit, bool refused)
    {
        // {"Id":333333} is 13 bytes.
        await using MessagingHost host = MessagingHost.Create(o => o.MaxMessageBytes = limit);

        Exception? thrown = await Record.ExceptionAsync(async () => await host.Services.GetRequiredService<IMessageBus>().Publish(new StockReport(333333)));

        Assert.Equal(refused, thrown is InvalidOperationException);
        Assert.Equal(refused ? 0 : 1, (await host.DrainAsync()).Length);
    }

    [Fact]
    public async Task EventWhoseLocalHandlerFails_IsNotSent()
    {
        await using MessagingHost host = MessagingHost.Create();
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new OrderPlaced(1, "fail")));

        Assert.Empty(await host.DrainAsync());
    }

    [Fact]
    public async Task MarkedEventWithNoLocalHandler_IsStillSent()
    {
        await using MessagingHost host = MessagingHost.Create();
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new OrderAudited(4));

        Assert.Equal("test.order-audited", Assert.Single(await host.DrainAsync()).Name);
    }

    [Fact]
    public async Task EventForwardedFromThePublisher_CarriesTheTransportsServiceNameAsItsOrigin()
    {
        await using MessagingHost host = MessagingHost.Create();
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new OrderPlaced(2, "none"));

        Assert.Equal("in-memory", host.Transport.ServiceName);
        Assert.Equal("in-memory", Assert.Single(await host.DrainAsync()).Origin);
    }

    [Fact]
    public async Task PublishOnlyType_IsSent_ButDeadLetteredWhenItArrives()
    {
        await using MessagingHost host = MessagingHost.Create();
        await host.Services.GetRequiredService<IMessageBus>().Publish(new StockReport(3));

        await host.RunRoundsAsync(1);

        Assert.Equal("This process has no handler for the message.", Assert.Single(host.DeadLettered).Reason);
    }
}
