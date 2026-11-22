using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SkathIO.Rogue.Jobs.Stores;

namespace SkathIO.Rogue.Jobs.SqlServer;

/// <summary>Registers the SQL Server job store.</summary>
public static class SqlServerJobsServiceCollectionExtensions
{
    /// <summary>
    /// Makes a <see cref="SqlServerJobStore"/> on the database <paramref name="connectionString"/> names
    /// the job store, in place of any store registered before, and registers two hosted services: the
    /// adaptive poller that wakes the worker when another process enqueues, and the pruner.
    /// <c>AddRogueJobs</c> must also be called, before or after. Only the first call's options apply.
    /// </summary>
    /// <exception cref="ArgumentException">The connection string is empty, or an option is out of range.</exception>
    public static IServiceCollection AddRogueSqlServerJobs(this IServiceCollection services, string connectionString, Action<SqlServerJobsOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        StoreSupport.AddDatabaseStore<SqlServerJobStore, SqlServerJobsOptions>(services, configure, (options, sp) => new SqlServerJobStore(connectionString, options, sp.GetRequiredService<TimeProvider>()), "SkathIO.Rogue.Jobs.SqlServer.SqlServerJobPruner");
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SqlServerJobPoller>(static sp => new SqlServerJobPoller(
            sp.GetRequiredService<SqlServerJobStore>(),
            sp.GetRequiredService<JobSignal>().Pulse,
            sp.GetRequiredService<TimeProvider>(),
            StoreSupport.Logger(sp, "SkathIO.Rogue.Jobs.SqlServer.SqlServerJobPoller"))));
        return services;
    }
}
