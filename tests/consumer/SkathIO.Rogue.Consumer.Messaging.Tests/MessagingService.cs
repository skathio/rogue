using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SkathIO.Rogue.Jobs;
using SkathIO.Rogue.Jobs.Postgres;
using SkathIO.Rogue.Messaging;
using SkathIO.Rogue.Messaging.Outbox;
using SkathIO.Rogue.Messaging.RabbitMQ;

namespace SkathIO.Rogue.Consumer.Messaging.Tests;

/// <summary>
/// One instance of a service, wired as an app wires messaging: the RabbitMQ transport on the shop's
/// exchange, a queue named after the service, and the outbox and inbox on the Postgres jobs store, in
/// a schema each service keeps for itself and its instances share.
/// </summary>
internal sealed class MessagingService : IAsyncDisposable
{
    /// <summary>The services' exchange.</summary>
    public const string Exchange = "shop";

    // Every instance of every service signs the delivery counts of its retried copies with this key, as
    // they would read it from one secret store. 32 bytes, the shortest the transport accepts.
    private const string SharedStampKey = "consumer-suite-shared-stamp-key!";

    private readonly ServiceProvider _services;
    private readonly List<IHostedService> _started = new List<IHostedService>();

    private MessagingService(ServiceProvider services, RabbitMqTransportOptions transport)
    {
        _services = services;
        Transport = transport;
    }

    public RabbitMqTransportOptions Transport { get; }

    public ShopProbe Probe => _services.GetRequiredService<ShopProbe>();

    public IMessageBus Bus => _services.GetRequiredService<IMessageBus>();

    public IMessageTransport Wire => _services.GetRequiredService<IMessageTransport>();

    public IMessageOutbox Outbox => _services.GetRequiredService<IMessageOutbox>();

    public MessageConsumer Consumer => _services.GetRequiredService<MessageConsumer>();

    public JobWorker Worker => _services.GetRequiredService<JobWorker>();

    public IServiceProvider Services => _services;

    /// <summary>
    /// Builds an instance of the service <paramref name="serviceName"/> in <paramref name="shop"/>. Its
    /// handlers run one at a time, a failed message is retried at once, and the broker sends it at most
    /// <paramref name="prefetchCount"/> unsettled deliveries. <paramref name="busyDelay"/> is how long a
    /// delivery whose id another instance is handling waits in the wait queue. The consumer is also
    /// registered as the hosted service a host starts and stops, which runs only after <see cref="StartAsync"/>.
    /// </summary>
    public static MessagingService StartService(Shop shop, string serviceName, int prefetchCount = 100, TimeSpan? busyDelay = null)
    {
        var transportOptions = new RabbitMqTransportOptions();
        var services = new ServiceCollection();
        services.AddSingleton<ShopProbe>();
        services.AddRogue();
        services.AddRogueMessaging(options =>
        {
            options.SerializerContext = ShopJson.Default;
            options.MaxConcurrency = 1;
            options.MaxAttempts = 3;
            // With no retry delay, a busy delivery waits out the poll interval instead.
            options.RetryDelay = TimeSpan.Zero;
            options.PollInterval = busyDelay ?? TimeSpan.FromSeconds(1);
        });
        services.AddRogueRabbitMqTransport(options =>
        {
            options.ConnectionUri = shop.Broker;
            options.Exchange = Exchange;
            options.ServiceName = serviceName;
            options.DeliveryStampKey = SharedStampKey;
            options.PrefetchCount = prefetchCount;
            transportOptions = options;
        });
        services.AddRoguePostgresJobs(shop.DataSource, options => options.Schema = serviceName + "_" + shop.Run);
        services.AddRogueJobs();
        services.AddRogueMessagingOutbox();
        services.AddRogueMessageConsumer();
        return new MessagingService(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), transportOptions);
    }

    /// <summary>Starts every hosted service, as a host does.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (IHostedService service in _services.GetServices<IHostedService>())
        {
            await service.StartAsync(cancellationToken).ConfigureAwait(false);
            _started.Add(service);
        }
    }

    /// <summary>Stops the hosted services in reverse order, as a host does on shutdown.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        for (int i = _started.Count - 1; i >= 0; i--)
        {
            await _started[i].StopAsync(cancellationToken).ConfigureAwait(false);
        }
        _started.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _services.DisposeAsync().ConfigureAwait(false);
    }
}
