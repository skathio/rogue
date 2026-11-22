using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Testcontainers.RabbitMq;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>
/// One RabbitMQ container for the collection, started only when its tests can run. Its AMQP port is
/// bound to a fixed host port, so a client reconnects to the same address after <see cref="RestartAsync"/>.
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private const int AmqpPort = 5672;
    private RabbitMqContainer? _container;

    public Uri ConnectionUri => new Uri(_container?.GetConnectionString() ?? throw new InvalidOperationException(RabbitMqFactAttribute.SkipReason()));

    public async Task InitializeAsync()
    {
        if (RabbitMqFactAttribute.SkipReason() is not null)
        {
            return;
        }
        // A quorum queue dead-letters at least once: when the target queue refuses a copy, the broker
        // sends it again only after this timeout, three minutes by default. That is longer than a test
        // waits for a deferred message to come back to a full queue, so the test broker retries sooner.
        _container = new RabbitMqBuilder("rabbitmq:4.1-alpine")
            .WithPortBinding(FreePort(), AmqpPort)
            .WithEnvironment("RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS", "-rabbit dead_letter_worker_publisher_confirm_timeout 5000")
            .Build();
        await _container.StartAsync();
    }

    /// <summary>Stops the broker and starts it again; durable queues and persistent messages survive.</summary>
    public async Task RestartAsync()
    {
        await _container!.StopAsync();
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

[CollectionDefinition(Name)]
public sealed class RabbitMqCollection : ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "rabbitmq";
}
