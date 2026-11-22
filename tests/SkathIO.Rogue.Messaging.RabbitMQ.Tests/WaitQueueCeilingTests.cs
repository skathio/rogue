using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// The wait queue has a message TTL of its own, the longest delay a deferral copy is given, so a
/// message put there with no expiration cannot hold its head, and every copy behind it, for good.
/// The broker refuses a redeclare whose TTL differs from the queue's, which is how the queue's
/// arguments are read here. Against a real broker in a container.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public sealed class WaitQueueCeilingTests
{
    private const string MessageName = "rmq.order-placed";

    private readonly RabbitMqFixture _broker;

    public WaitQueueCeilingTests(RabbitMqFixture broker) => _broker = broker;

    [RabbitMqFact]
    public async Task TheWaitQueue_HasTheLongestDelay_AsItsMessageTtl()
    {
        RabbitMqTransportOptions options = await DeclaredAsync();

        Exception? refused = await RedeclareAsync(options, 86_400_000);

        Assert.Null(refused);
    }

    [RabbitMqFact]
    public async Task ARedeclare_WithAnotherMessageTtl_IsRefused()
    {
        RabbitMqTransportOptions options = await DeclaredAsync();

        Exception? refused = await RedeclareAsync(options, 3_600_000);

        Assert.IsType<OperationInterruptedException>(refused);
    }

    // Sending connects the transport, which declares its topology; nothing is bound to this name.
    private async Task<RabbitMqTransportOptions> DeclaredAsync()
    {
        string run = Guid.NewGuid().ToString("N");
        var options = new RabbitMqTransportOptions
        {
            ConnectionUri = _broker.ConnectionUri,
            Exchange = "test-" + run,
            ServiceName = "service-" + run,
            DeliveryStampKey = RabbitMqHost.DeliveryStampKey,
        };
        await using var transport = new RabbitMqTransport(options, new[] { MessageName });
        await transport.SendAsync(new[] { new MessageEnvelope { Id = "connect", Name = "rmq.report", Body = Encoding.UTF8.GetBytes("{}") } }, CancellationToken.None);
        return options;
    }

    private async Task<Exception?> RedeclareAsync(RabbitMqTransportOptions options, int messageTtl)
    {
        (IConnection connection, IChannel raw) = await RabbitMqHost.RawChannelAsync(_broker.ConnectionUri);
        await using (connection)
        {
            try
            {
                await raw.QueueDeclareAsync(options.WaitQueue!, true, false, false, new Dictionary<string, object?>
                {
                    ["x-queue-type"] = "quorum",
                    ["x-dead-letter-exchange"] = string.Empty,
                    ["x-dead-letter-routing-key"] = options.ServiceName!,
                    ["x-dead-letter-strategy"] = "at-least-once",
                    ["x-overflow"] = "reject-publish",
                    ["x-message-ttl"] = messageTtl,
                });
                return null;
            }
            catch (OperationInterruptedException ex)
            {
                return ex;
            }
        }
    }
}
