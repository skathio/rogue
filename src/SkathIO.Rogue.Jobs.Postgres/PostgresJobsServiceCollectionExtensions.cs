using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace SkathIO.Rogue.Jobs.Postgres;

/// <summary>Registers the Postgres job store.</summary>
public static class PostgresJobsServiceCollectionExtensions
{
    /// <summary>
    /// Makes a <see cref="PostgresJobStore"/> on <paramref name="dataSource"/> the job store, in place
    /// of any store registered before, and registers <see cref="IPostgresJobScheduler"/> plus two hosted
    /// services: one that LISTENs for enqueues and wakes the worker, and the pruner.
    /// <c>AddRogueJobs</c> must also be called, before or after. Only the first call's options apply.
    /// </summary>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    public static IServiceCollection AddRoguePostgresJobs(this IServiceCollection services, NpgsqlDataSource dataSource, Action<PostgresJobsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);
        var options = new PostgresJobsOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new PostgresJobStore(dataSource, options, sp.GetRequiredService<TimeProvider>()));
        services.Replace(ServiceDescriptor.Singleton<IJobStore>(static sp => sp.GetRequiredService<PostgresJobStore>()));
        services.TryAddSingleton<IPostgresJobScheduler>(static sp => new PostgresJobScheduler(
            sp.GetRequiredService<IJobScheduler>(),
            sp.GetRequiredService<PostgresJobStore>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PostgresJobListener>(static sp => new PostgresJobListener(
            sp.GetRequiredService<PostgresJobStore>(),
            sp.GetRequiredService<JobSignal>().Pulse,
            sp.GetRequiredService<TimeProvider>(),
            Logger(sp, "SkathIO.Rogue.Jobs.Postgres.PostgresJobListener"))));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PostgresJobPruner>(static sp => new PostgresJobPruner(
            sp.GetRequiredService<PostgresJobStore>(),
            sp.GetRequiredService<TimeProvider>(),
            Logger(sp, "SkathIO.Rogue.Jobs.Postgres.PostgresJobPruner"))));
        return services;
    }

    private static ILogger Logger(IServiceProvider services, string category)
        => (ILogger?)services.GetService<ILoggerFactory>()?.CreateLogger(category) ?? NullLogger.Instance;
}
