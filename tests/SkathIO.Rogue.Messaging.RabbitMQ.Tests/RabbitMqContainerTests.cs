using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using RabbitMQ.Client;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>The transport against a real broker in a container: one per collection, skipped without Docker.</summary>
[Collection(RabbitMqCollection.Name)]
public sealed class RabbitMqContainerTests
{
    private readonly RabbitMqFixture _broker;

    public RabbitMqContainerTests(RabbitMqFixture broker) => _broker = broker;

    [RabbitMqFact]
    public async Task FailingHandler_IsRedelivered_ThenDeadLetteredWithItsReason()
    {
        await using RabbitMqHost host = RabbitMqHost.Create(_broker.ConnectionUri);
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await host.Consumer.RunOnceAsync();
            await host.Bus.Publish(new OrderPlaced(1, "fail"));

            BasicGetResult? deadLettered = null;
            await host.RunUntilAsync(() => deadLettered is not null, async () => deadLettered = await raw.BasicGetAsync(host.Options.DeadLetterQueue!, true));

            // Delivered three times (the first and two redeliveries), ack only after success means none
            // of those removed it, and then it moved to the dead-letter queue with its reason.
            Assert.Equal(3, host.Probe.Count("placed:1"));
            Assert.Equal("rmq.order-placed", deadLettered!.BasicProperties.Type);
            Assert.Equal(3, Assert.IsType<int>(deadLettered.BasicProperties.Headers!["rogue-delivery-count"]));
            string reason = Encoding.UTF8.GetString((byte[])deadLettered.BasicProperties.Headers["rogue-dead-letter-reason"]!);
            Assert.Contains("delivery 3 of 3", reason, StringComparison.Ordinal);
            Assert.EndsWith("System.InvalidOperationException", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("handler failed", reason, StringComparison.Ordinal);
            Assert.Equal("{\"Id\":1,\"Note\":\"fail\"}", Encoding.UTF8.GetString(deadLettered.Body.Span));
            Assert.Equal(0u, await raw.MessageCountAsync(host.Options.ServiceName!));
        }
    }

    [RabbitMqFact]
    public async Task ForeignJson_IsMappedByRoutingKeyOrHeader_ToItsHandler_AndAnUnmappedOneIsDeadLettered()
    {
        string legacy = "legacy-" + Guid.NewGuid().ToString("N");
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            await raw.ExchangeDeclareAsync(legacy, ExchangeType.Topic, false, true);
            await using RabbitMqHost host = RabbitMqHost.Create(_broker.ConnectionUri, options => options
                .MapRoutingKey(legacy, "legacy.order.placed", "rmq.order-placed")
                .Bind(legacy, "legacy.events.#")
                .MapHeader("event-type", "StockReserved", "rmq.reserve"));
            await host.Consumer.RunOnceAsync();

            // Published as another system would: plain JSON, no Rogue properties or headers.
            await raw.BasicPublishAsync(legacy, "legacy.order.placed", Encoding.UTF8.GetBytes("{\"Id\":7,\"Note\":\"foreign\"}"));
            await raw.BasicPublishAsync(legacy, "legacy.events.v1", false, Headers("event-type", "StockReserved"), Encoding.UTF8.GetBytes("{\"Id\":9}"));
            await raw.BasicPublishAsync(legacy, "legacy.events.v1", false, Headers("event-type", "SomethingElse"), Encoding.UTF8.GetBytes("{\"Id\":11}"));

            BasicGetResult? deadLettered = null;
            await host.RunUntilAsync(
                () => host.Probe.Count("placed:7") == 1 && host.Probe.Count("reserve:9") == 1 && deadLettered is not null,
                async () => deadLettered ??= await raw.BasicGetAsync(host.Options.DeadLetterQueue!, true));

            Assert.Equal(new[] { "placed:7", "reserve:9" }, Sorted(host.Probe.Ran));
            Assert.Equal("{\"Id\":11}", Encoding.UTF8.GetString(deadLettered!.Body.Span));
            Assert.Contains("No message type has this name", Encoding.UTF8.GetString((byte[])deadLettered.BasicProperties.Headers!["rogue-dead-letter-reason"]!), StringComparison.Ordinal);
        }
    }

    [RabbitMqFact]
    public async Task BrokerRestart_IsSurvived_WithoutRebuildingTheTransport()
    {
        await using RabbitMqHost host = RabbitMqHost.Create(_broker.ConnectionUri);
        await host.RunUntilAsync(() => host.Probe.Count("placed:1") == 1, Once(() => host.Bus.Publish(new OrderPlaced(1, "ok")).AsTask()));

        // Published before the restart and not handled yet: the durable queue keeps it.
        await host.Bus.Publish(new OrderPlaced(2, "ok"));
        await _broker.RestartAsync();

        // The same transport reconnects, publishes and consumes again once the broker is back.
        await host.RunUntilAsync(
            () => host.Probe.Count("placed:2") >= 1 && host.Probe.Count("placed:3") >= 1,
            Once(() => host.Bus.Publish(new OrderPlaced(3, "ok")).AsTask()));

        Assert.Equal(1, host.Probe.Count("placed:1"));
    }

    private static BasicProperties Headers(string header, string value) =>
        new BasicProperties { Headers = new Dictionary<string, object?> { [header] = value } };

    private static string[] Sorted(string[] values)
    {
        Array.Sort(values, StringComparer.Ordinal);
        return values;
    }

    // A step that runs until it first succeeds, then does nothing.
    private static Func<Task> Once(Func<Task> step)
    {
        bool done = false;
        return async () =>
        {
            if (!done)
            {
                await step();
                done = true;
            }
        };
    }
}
