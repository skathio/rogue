using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SkathIO.Rogue.Jobs.Stores;

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
        ArgumentNullException.ThrowIfNull(dataSource);
        StoreSupport.AddDatabaseStore<PostgresJobStore, PostgresJobsOptions>(services, configure, (options, sp) => new PostgresJobStore(dataSource, options, sp.GetRequiredService<TimeProvider>()), "SkathIO.Rogue.Jobs.Postgres.PostgresJobPruner");
        services.TryAddSingleton<IPostgresJobScheduler>(static sp => new PostgresJobScheduler(
            sp.GetRequiredService<IJobScheduler>(),
            sp.GetRequiredService<PostgresJobStore>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PostgresJobListener>(static sp => new PostgresJobListener(
            sp.GetRequiredService<PostgresJobStore>(),
            sp.GetRequiredService<JobSignal>().Pulse,
            sp.GetRequiredService<TimeProvider>(),
            StoreSupport.Logger(sp, "SkathIO.Rogue.Jobs.Postgres.PostgresJobListener"))));
        return services;
    }
}
