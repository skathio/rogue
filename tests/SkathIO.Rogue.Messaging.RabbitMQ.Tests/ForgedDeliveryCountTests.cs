using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using RabbitMQ.Client;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// A sender cannot switch off a message's retries by putting a delivery count header on it: the
/// attempts come from state the consumer controls, so a failing handler still runs its full number of
/// attempts. Against a real broker; skipped without Docker, always run in CI.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class ForgedDeliveryCountTests
{
    // The attempts RabbitMqHost allows a message.
    private const int MaxAttempts = 3;

    private readonly RabbitMqFixture _broker;

    public ForgedDeliveryCountTests(RabbitMqFixture broker) => _broker = broker;

    /// <summary>
    /// The header value a sender forges (null: no header), at the ends of the attempt limit and of the
    /// range the header could name, and whether it is published to the service's own exchange or
    /// straight to its queue through the default exchange, as the transport's own retry copies are.
    /// </summary>
    public static TheoryData<object?, bool> ForgedCounts()
    {
        object?[] values =
        {
            null,
            1,
            MaxAttempts - 1,
            MaxAttempts,
            MaxAttempts + 1,
            RabbitMqWire.MaxDeliveryCount,
            RabbitMqWire.MaxDeliveryCount + 1,
            int.MaxValue,
            long.MaxValue,
        };
        var data = new TheoryData<object?, bool>();
        foreach (object? value in values)
        {
            data.Add(value, true);
            data.Add(value, false);
        }
        return data;
    }

    [RabbitMqTheory]
    [MemberData(nameof(ForgedCounts))]
    public async Task AFailingMessage_WithAForgedDeliveryCount_StillRunsEveryAttempt_ThenIsDeadLettered(object? forged, bool viaOwnExchange)
    {
        await using RabbitMqHost host = RabbitMqHost.Create(_broker.ConnectionUri);
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            // Declares and binds the service's queue before anything is published.
            await host.Consumer.RunOnceAsync();
            var properties = new BasicProperties
            {
                MessageId = Guid.NewGuid().ToString("N"),
                Type = "rmq.order-placed",
                ContentType = MessageEnvelope.JsonContentType,
                Headers = new Dictionary<string, object?>(),
            };
            if (forged is not null)
            {
                properties.Headers[RabbitMqWire.DeliveryCountHeader] = forged;
            }
            byte[] body = Encoding.UTF8.GetBytes("{\"Id\":21,\"Note\":\"fail\"}");
            if (viaOwnExchange)
            {
                await raw.BasicPublishAsync(host.Options.Exchange, "rmq.order-placed", false, properties, body);
            }
            else
            {
                await raw.BasicPublishAsync(string.Empty, host.Options.ServiceName!, false, properties, body);
            }

            BasicGetResult? deadLettered = null;
            await host.RunUntilAsync(() => deadLettered is not null, async () => deadLettered = await raw.BasicGetAsync(host.Options.DeadLetterQueue!, true));

            Assert.Equal(MaxAttempts, host.Probe.Count("placed:21"));
            Assert.Equal("{\"Id\":21,\"Note\":\"fail\"}", Encoding.UTF8.GetString(deadLettered!.Body.Span));
            Assert.Equal(0u, await raw.MessageCountAsync(host.Options.ServiceName!));
        }
    }
}

/// <summary>A theory that needs a RabbitMQ container, skipped exactly when a <see cref="RabbitMqFactAttribute"/> is.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RabbitMqTheoryAttribute : TheoryAttribute
{
    public RabbitMqTheoryAttribute()
    {
        if (RabbitMqFactAttribute.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}
