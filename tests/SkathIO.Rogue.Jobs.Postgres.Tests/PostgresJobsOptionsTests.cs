using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace SkathIO.Rogue.Jobs.Postgres.Tests;

/// <summary>Options and registration: checked without a database.</summary>
public sealed class PostgresJobsOptionsTests
{
    [Theory]
    [InlineData("rogue_jobs")]
    [InlineData("_x")]
    [InlineData("a")]
    [InlineData("a1234567890123456789012345678901234567890123456789")]
    public void Schema_AcceptsPlainLowerCaseNames_UpToTheLimit(string schema)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=localhost");

        var store = new PostgresJobStore(dataSource, new PostgresJobsOptions { Schema = schema });

        Assert.Equal(schema, store.Options.Schema);
        Assert.True(schema.Length <= PostgresJobsOptions.MaxSchemaLength);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Rogue")]
    [InlineData("1jobs")]
    [InlineData("jobs;select 1")]
    [InlineData("jo\"bs")]
    [InlineData("jobs-x")]
    [InlineData("ab234567890123456789012345678901234567890123456789x")]
    public void Schema_RejectsAnythingThatWouldNeedQuoting_OrIsTooLong(string schema)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=localhost");

        Assert.Throws<ArgumentException>(() => new PostgresJobStore(dataSource, new PostgresJobsOptions { Schema = schema }));
    }

    [Fact]
    public void Schema_RejectsNull()
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=localhost");

        Assert.Throws<ArgumentNullException>(() => new PostgresJobStore(dataSource, new PostgresJobsOptions { Schema = null! }));
    }

    [Fact]
    public void Durations_MustBePositive_AndTheBatchAtLeastOne()
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=localhost");
        Action<PostgresJobsOptions>[] invalid =
        {
            static o => o.SucceededRetention = TimeSpan.Zero,
            static o => o.DeadLetteredRetention = TimeSpan.Zero,
            static o => o.PruneInterval = TimeSpan.Zero,
            static o => o.ListenReconnectDelay = TimeSpan.Zero,
            static o => o.PruneBatchSize = 0,
        };

        Assert.All(invalid, set =>
        {
            var options = new PostgresJobsOptions();
            set(options);
            Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresJobStore(dataSource, options));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddRoguePostgresJobs(dataSource, set));
        });
        var smallest = new PostgresJobStore(dataSource, new PostgresJobsOptions
        {
            SucceededRetention = TimeSpan.FromTicks(1),
            DeadLetteredRetention = TimeSpan.FromTicks(1),
            PruneInterval = TimeSpan.FromTicks(1),
            ListenReconnectDelay = TimeSpan.FromTicks(1),
            PruneBatchSize = 1,
        });
        Assert.Equal(1, smallest.Options.PruneBatchSize);
    }

    [Fact]
    public void TheStoreKeepsItsOwnCopyOfTheOptions()
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=localhost");
        var options = new PostgresJobsOptions();
        var store = new PostgresJobStore(dataSource, options);

        options.Schema = "Not Valid";

        Assert.Equal("rogue_jobs", store.Options.Schema);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Registration_MakesPostgresTheStore_BeforeOrAfterAddRogueJobs(bool postgresFirst)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=localhost");
        var services = new ServiceCollection();
        if (postgresFirst)
        {
            services.AddRoguePostgresJobs(dataSource);
        }
        services.AddRogue();
        services.AddRogueJobs(static o => o.SerializerContext = PostgresTestJson.Default);
        services.AddRoguePostgresJobs(dataSource);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<PostgresJobStore>(), provider.GetRequiredService<IJobStore>());
        Assert.NotNull(provider.GetRequiredService<IPostgresJobScheduler>());
        Assert.Single(provider.GetServices<IHostedService>(), static s => s is PostgresJobListener);
        Assert.Single(provider.GetServices<IHostedService>(), static s => s is PostgresJobPruner);
    }

    [Fact]
    public void Registration_RejectsNullArguments()
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=localhost");

        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddRoguePostgresJobs(null!));
        Assert.Throws<ArgumentNullException>(() => PostgresJobsServiceCollectionExtensions.AddRoguePostgresJobs(null!, dataSource));
        Assert.Throws<ArgumentNullException>(() => new PostgresJobStore(null!));
    }
}
