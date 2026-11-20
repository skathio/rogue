using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Xunit;

namespace SkathIO.Rogue.Messaging.RabbitMQ.Tests;

/// <summary>A service wired as an application would wire it, on its own exchange and queue.</summary>
internal sealed class RabbitMqHost : IAsyncDisposable
{
    // Only a bound on how long a broken test waits; no assertion depends on time.
    private static readonly TimeSpan _giveUpAfter = TimeSpan.FromMinutes(2);

    private RabbitMqHost(ServiceProvider services, RabbitMqTransportOptions options)
    {
        Services = services;
        Options = options;
    }

    public ServiceProvider Services { get; }

    public RabbitMqTransportOptions Options { get; }

    public MessageProbe Probe => Services.GetRequiredService<MessageProbe>();

    public MessageConsumer Consumer => Services.GetRequiredService<MessageConsumer>();

    public IMessageBus Bus => Services.GetRequiredService<IMessageBus>();

    public static RabbitMqHost Create(Uri broker, Action<RabbitMqTransportOptions>? configure = null)
    {
        string run = Guid.NewGuid().ToString("N");
        var transportOptions = new RabbitMqTransportOptions();
        var collection = new ServiceCollection();
        collection.AddSingleton<MessageProbe>();
        collection.AddRogue();
        collection.AddRogueMessaging(static options =>
        {
            options.SerializerContext = TestMessagesJson.Default;
            options.MaxAttempts = 3;
            options.RetryDelay = TimeSpan.Zero;
        });
        collection.AddRogueRabbitMqTransport(options =>
        {
            options.ConnectionUri = broker;
            options.Exchange = "test-" + run;
            options.ServiceName = "service-" + run;
            options.ConfigureConnection = static factory => factory.NetworkRecoveryInterval = TimeSpan.FromSeconds(1);
            configure?.Invoke(options);
            transportOptions = options;
        });
        return new RabbitMqHost(collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), transportOptions);
    }

    /// <summary>
    /// Runs the consumer, and <paramref name="step"/> before each round, until <paramref name="done"/>.
    /// A round that fails (the broker is away) is tried again.
    /// </summary>
    public async Task RunUntilAsync(Func<bool> done, Func<Task>? step = null)
    {
        DateTime giveUp = DateTime.UtcNow + _giveUpAfter;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < giveUp, "Gave up waiting; handlers ran: " + string.Join(", ", Probe.Ran));
            try
            {
                if (step is not null)
                {
                    await step();
                }
                await Consumer.RunOnceAsync();
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
        }
    }

    /// <summary>A channel of a separate connection, as another system or an operator would use.</summary>
    public static async Task<(IConnection Connection, IChannel Channel)> RawChannelAsync(Uri broker)
    {
        IConnection connection = await new ConnectionFactory { Uri = broker }.CreateConnectionAsync();
        return (connection, await connection.CreateChannelAsync());
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
