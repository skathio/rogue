using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkathIO.Rogue.Jobs;

namespace SkathIO.Rogue.Messaging.Outbox;

/// <summary>Registers the outbox and the inbox.</summary>
public static class MessageOutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMessageOutbox"/>, the job that relays its messages, and an
    /// <see cref="IMessageInbox"/> kept by the job store, which the <see cref="MessageConsumer"/> then
    /// uses to run the handlers of a message delivered twice once. <c>AddRogueMessaging</c> and
    /// <c>AddRogueJobs</c> must also be called, with a database jobs store such as
    /// <c>AddRoguePostgresJobs</c> or <c>AddRogueSqlServerJobs</c>; relays run in hosts that call
    /// <c>AddRogueJobWorker</c>, on the jobs' default queue. Only the first call's options apply.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static IServiceCollection AddRogueMessagingOutbox(this IServiceCollection services, Action<MessageOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new MessageOutboxOptions();
        configure?.Invoke(options);
        options.Validate();

        // The first call's options mark the outbox registered, so the relay job is added once however
        // often this is called.
        foreach (ServiceDescriptor registered in services)
        {
            if (registered.ServiceType == typeof(MessageOutboxOptions))
            {
                return services;
            }
        }
        services.AddSingleton(options);
        services.TryAddSingleton<IMessageOutbox>(static sp => Create(sp));
        services.TryAddSingleton<IMessageInbox>(static sp => new JobStoreInbox(sp.GetRequiredService<IJobStore>(), sp.GetRequiredService<MessageOutboxOptions>().InboxLease));
        services.Add(ServiceDescriptor.Singleton<JobDefinition>(static sp => OutboxRelay.Definition(sp)));
        return services;
    }

    private static MessageOutbox Create(IServiceProvider services) => new MessageOutbox(
        services.GetRequiredService<IMessageBus>(),
        services.GetRequiredService<IJobScheduler>(),
        services.GetRequiredService<IJobStore>());
}
