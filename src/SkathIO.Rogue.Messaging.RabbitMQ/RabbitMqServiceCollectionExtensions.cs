using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SkathIO.Rogue.Messaging.RabbitMQ;

/// <summary>Registers the RabbitMQ transport.</summary>
public static class RabbitMqServiceCollectionExtensions
{
    /// <summary>
    /// Makes a <see cref="RabbitMqTransport"/> the message transport, in place of any transport
    /// registered before. When <see cref="RabbitMqTransportOptions.ServiceName"/> is set, the service's
    /// queue is bound to the name of every event and command marked as a message in this process, as
    /// the source generator found them. <c>AddRogueMessaging</c> must also be called, before or after. The options
    /// are checked here; the broker is first reached when a message is sent or received.
    /// </summary>
    /// <exception cref="ArgumentException">An option is not valid.</exception>
    public static IServiceCollection AddRogueRabbitMqTransport(this IServiceCollection services, Action<RabbitMqTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RabbitMqTransportOptions();
        configure(options);
        options.Validate();

        services.TryAddSingleton(sp => new RabbitMqTransport(
            options,
            sp.GetServices<MessageBinding>().Where(static b => b.CanDispatch).Select(static b => b.Name)));
        services.Replace(ServiceDescriptor.Singleton<IMessageTransport>(static sp => sp.GetRequiredService<RabbitMqTransport>()));
        return services;
    }
}
