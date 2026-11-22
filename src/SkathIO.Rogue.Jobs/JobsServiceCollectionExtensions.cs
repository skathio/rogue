using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
        services.TryAddSingleton(sp => new JobRegistry(sp.GetServices<JobDefinition>(), options, sp.GetServices<RecurringJob>()));
        services.TryAddSingleton(static sp => new JobScheduler(
            sp.GetRequiredService<JobRegistry>(),
            sp.GetRequiredService<IJobStore>(),
            sp.GetRequiredService<JobSignal>()));
        services.TryAddSingleton<IJobScheduler>(static sp => sp.GetRequiredService<JobScheduler>());
        services.TryAddSingleton(static sp => new JobWorker(sp.GetRequiredService<JobRegistry>(), sp.GetRequiredService<IJobStore>(), sp));
        return services;
    }

    /// <summary>
    /// Registers the hosted services that run jobs while the host runs: the <see cref="JobWorker"/>, and
    /// the scheduler that stores each recurring job's next occurrence while this host leads the others.
    /// Call it in each host that should run jobs, together with <see cref="AddRogueJobs"/>; calling it
    /// again adds no second worker.
    /// </summary>
    public static IServiceCollection AddRogueJobWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JobWorkerService>(static sp => new JobWorkerService(sp.GetRequiredService<JobWorker>())));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RecurringJobService>(static sp => new RecurringJobService(
            sp.GetRequiredService<JobRegistry>(),
            sp.GetRequiredService<JobScheduler>(),
            sp.GetRequiredService<TimeProvider>(),
            (ILogger?)sp.GetService<ILoggerFactory>()?.CreateLogger("SkathIO.Rogue.Jobs.RecurringJobs") ?? NullLogger.Instance)));
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

    /// <summary>
    /// Registers <paramref name="job"/> to run at each occurrence of its cron expression in hosts that
    /// call <see cref="AddRogueJobWorker"/>. Generated code calls this for each command marked
    /// <see cref="RecurringAttribute"/>; a command can have one schedule, and a second fails the host at
    /// startup.
    /// </summary>
    public static IServiceCollection AddRecurringJob(this IServiceCollection services, RecurringJob job)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(job);
        services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(RecurringJob), job));
        return services;
    }
}
