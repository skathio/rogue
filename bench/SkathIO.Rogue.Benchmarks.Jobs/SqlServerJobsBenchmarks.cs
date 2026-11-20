using System;
using BenchmarkDotNet.Attributes;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs.SqlServer;

namespace SkathIO.Rogue.JobsBenchmarks;

/// <summary>
/// Rogue's SQL Server store against Hangfire.SqlServer, both on the same database: the one the
/// <c>ROGUE_BENCH_SQLSERVER</c> environment variable's connection string names, each in a schema of
/// its own. Not part of the CI smoke, since it needs a database; run it with
/// <c>--filter '*SqlServerJobsBenchmarks*'</c>.
/// </summary>
[MemoryDiagnoser]
public class SqlServerJobsBenchmarks : JobsBenchmarks
{
    /// <summary>The environment variable holding the connection string of the database both use.</summary>
    public const string ConnectionStringVariable = "ROGUE_BENCH_SQLSERVER";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionStringVariable) is { Length: > 0 } connectionString
            ? connectionString
            : throw new InvalidOperationException("Set " + ConnectionStringVariable + " to the connection string of a SQL Server database to run this benchmark.");

    /// <inheritdoc />
    protected override JobStorage CreateHangfireStorage()
    {
        string connectionString = ConnectionString;

        // The settings Hangfire's documentation recommends for SQL Server: no polling delay, and
        // fetched jobs held by a sliding invisibility timeout instead of a transaction.
        return new SqlServerStorage(() => new SqlConnection(connectionString), new SqlServerStorageOptions
        {
            SchemaName = "rogue_bench_hangfire",
            QueuePollInterval = TimeSpan.Zero,
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            UseRecommendedIsolationLevel = true,
            DisableGlobalLocks = true,
        });
    }

    /// <inheritdoc />
    protected override void AddRogueStore(IServiceCollection services) =>
        services.AddRogueSqlServerJobs(ConnectionString, static options => options.Schema = "rogue_bench");
}
