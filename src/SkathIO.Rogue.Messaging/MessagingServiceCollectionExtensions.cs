using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace SkathIO.Rogue.Messaging;

/// <summary>Registers the message bus and, in a host that asks for it, the consumer.</summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMessageBus"/> and <see cref="MessageConsumer"/>, and turns on the sending of
    /// events marked <see cref="MessageAttribute"/> when they are published through
    /// <see cref="IPublisher"/>. An <see cref="IMessageTransport"/> must also be registered, such as an
    /// <see cref="InMemoryTransport"/> in tests. Nothing is consumed until
    /// <see cref="AddRogueMessageConsumer"/> is also called. The message types come from the code the
    /// Rogue source generator emits into each project that references this package, registered by
    /// <c>AddRogue</c>, which must also be called. Only the first call's options apply.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static IServiceCollection AddRogueMessaging(this IServiceCollection services, Action<MessagingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        MessagingOptions options = MessagingOptions.Create(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new MessageRegistry(sp.GetServices<MessageBinding>(), options));
        services.TryAddSingleton(static sp => new MessageConsumer(sp.GetRequiredService<MessageRegistry>(), sp));
        services.TryAddSingleton(static sp => new MessageBus(sp));
        return services.AddSingletonAlias<IMessageBus, MessageBus>();
    }

    /// <summary>
    /// Registers the hosted service that runs the <see cref="MessageConsumer"/> while the host runs. Call
    /// it in each host that should consume messages, together with <see cref="AddRogueMessaging"/>;
    /// calling it again adds no second consumer.
    /// </summary>
    public static IServiceCollection AddRogueMessageConsumer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MessageConsumerService>(static sp => new MessageConsumerService(sp.GetRequiredService<MessageConsumer>())));
        return services;
    }

    // One instance behind both service types; the first registration of TService is kept.
    private static IServiceCollection AddSingletonAlias<TService, TImplementation>(this IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        services.TryAddSingleton<TService>(static sp => sp.GetRequiredService<TImplementation>());
        return services;
    }

    /// <summary>
    /// Registers <paramref name="binding"/>. Generated code calls this for each message type; a binding
    /// already registered for the same type is kept.
    /// </summary>
    public static void AddMessageBinding(this IServiceCollection services, MessageBinding binding)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(binding);
        services.TryAddEnumerable(new ServiceDescriptor(typeof(MessageBinding), binding));
    }
}
