using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace SkathIO.Rogue.Jobs;

/// <summary>Registers the job scheduler and, in a host that asks for it, the worker.</summary>
public static class JobsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IJobScheduler"/>, <see cref="JobWorker"/> and, unless a store is already
    /// registered, an <see cref="InMemoryJobStore"/>: enough to enqueue jobs. No worker runs until
    /// <see cref="AddRogueJobWorker"/> is also called, so a host that only enqueues never runs jobs.
    /// The job kinds come from the code the Rogue source generator emits into each project that
    /// references this package, registered by <c>AddRogue</c>, which must also be called. Only the
    /// first call's options apply.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static IServiceCollection AddRogueJobs(this IServiceCollection services, Action<JobsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new JobsOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJobStore>(static sp => new InMemoryJobStore(sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(static _ => new JobSignal());
        services.TryAddSingleton(sp => new JobRegistry(sp.GetServices<JobDefinition>(), options));
        services.TryAddSingleton<IJobScheduler>(static sp => new JobScheduler(
            sp.GetRequiredService<JobRegistry>(),
            sp.GetRequiredService<IJobStore>(),
            sp.GetRequiredService<JobSignal>()));
        services.TryAddSingleton(static sp => new JobWorker(sp.GetRequiredService<JobRegistry>(), sp.GetRequiredService<IJobStore>(), sp));
        return services;
    }

    /// <summary>
    /// Registers the hosted service that runs the <see cref="JobWorker"/> while the host runs. Call it
    /// in each host that should run jobs, together with <see cref="AddRogueJobs"/>; calling it again
    /// adds no second worker.
    /// </summary>
    public static IServiceCollection AddRogueJobWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JobWorkerService>(static sp => new JobWorkerService(sp.GetRequiredService<JobWorker>())));
        return services;
    }

    /// <summary>
    /// Registers <paramref name="definition"/>. Generated code calls this for each command; a definition
    /// already registered for the same command is kept.
    /// </summary>
    public static void AddJobDefinition(this IServiceCollection services, JobDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(definition);
        services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(JobDefinition), definition));
    }
}
