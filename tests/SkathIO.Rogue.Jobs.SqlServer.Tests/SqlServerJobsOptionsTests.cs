using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SkathIO.Rogue.Jobs.Stores;
using Xunit;

namespace SkathIO.Rogue.Jobs.SqlServer.Tests;

/// <summary>Options, registration and the poller's backoff: checked without a database.</summary>
public sealed class SqlServerJobsOptionsTests
{
    private const string ConnectionString = "Server=localhost;Database=jobs;TrustServerCertificate=True";

    [Theory]
    [InlineData("rogue_jobs")]
    [InlineData("_x")]
    [InlineData("a")]
    [InlineData("a1234567890123456789012345678901234567890123456789")]
    public void Schema_AcceptsPlainLowerCaseNames_UpToTheLimit(string schema)
    {
        var store = new SqlServerJobStore(ConnectionString, new SqlServerJobsOptions { Schema = schema });

        Assert.Equal(schema, store.Options.Schema);
        Assert.True(schema.Length <= SqlServerJobsOptions.MaxSchemaLength);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Rogue")]
    [InlineData("1jobs")]
    [InlineData("jobs;select 1")]
    [InlineData("jo]bs")]
    [InlineData("jo'bs")]
    [InlineData("jobs-x")]
    [InlineData("rogue_jobs\n")]
    [InlineData("ab234567890123456789012345678901234567890123456789x")]
    public void Schema_RejectsAnythingThatWouldNeedQuoting_OrIsTooLong(string schema)
    {
        Assert.Throws<ArgumentException>(() => new SqlServerJobStore(ConnectionString, new SqlServerJobsOptions { Schema = schema }));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddRogueSqlServerJobs(ConnectionString, o => o.Schema = schema));
    }

    [Fact]
    public void Schema_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new SqlServerJobStore(ConnectionString, new SqlServerJobsOptions { Schema = null! }));
    }

    [Fact]
    public void Durations_MustBePositive_TheBatchAtLeastOne_AndTheMaxPollNoShorterThanTheMin()
    {
        Action<SqlServerJobsOptions>[] invalid =
        {
            static o => o.SucceededRetention = TimeSpan.Zero,
            static o => o.DeadLetteredRetention = TimeSpan.Zero,
            static o => o.PruneInterval = TimeSpan.Zero,
            static o => o.PruneBatchSize = 0,
            static o => o.MinPollInterval = TimeSpan.Zero,
            static o => o.MaxPollInterval = o.MinPollInterval - TimeSpan.FromTicks(1),
        };

        Assert.All(invalid, set =>
        {
            var options = new SqlServerJobsOptions();
            set(options);
            Assert.Throws<ArgumentOutOfRangeException>(() => new SqlServerJobStore(ConnectionString, options));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddRogueSqlServerJobs(ConnectionString, set));
        });
        var smallest = new SqlServerJobStore(ConnectionString, new SqlServerJobsOptions
        {
            SucceededRetention = TimeSpan.FromTicks(1),
            DeadLetteredRetention = TimeSpan.FromTicks(1),
            PruneInterval = TimeSpan.FromTicks(1),
            PruneBatchSize = 1,
            MinPollInterval = TimeSpan.FromTicks(1),
            MaxPollInterval = TimeSpan.FromTicks(1),
        });
        Assert.Equal(smallest.Options.MinPollInterval, smallest.Options.MaxPollInterval);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ConnectionString_IsRequired(string? connectionString)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SqlServerJobStore(connectionString!));
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddRogueSqlServerJobs(connectionString!));
    }

    [Fact]
    public void TheStoreKeepsItsOwnCopyOfTheOptions()
    {
        var options = new SqlServerJobsOptions();
        var store = new SqlServerJobStore(ConnectionString, options);

        options.Schema = "Not Valid";

        Assert.Equal("rogue_jobs", store.Options.Schema);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Registration_MakesSqlServerTheStore_BeforeOrAfterAddRogueJobs_WithOnePollerAndPruner(bool sqlServerFirst)
    {
        var services = new ServiceCollection();
        if (sqlServerFirst)
        {
            services.AddRogueSqlServerJobs(ConnectionString);
        }
        services.AddRogueJobs();
        services.AddRogueSqlServerJobs(ConnectionString);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<SqlServerJobStore>(), provider.GetRequiredService<IJobStore>());
        Assert.Single(provider.GetServices<IHostedService>(), static s => s is SqlServerJobPoller);
        Assert.Single(provider.GetServices<IHostedService>(), static s => s is JobPruner);
    }

    [Fact]
    public void Registration_RejectsNullServices()
    {
        Assert.Throws<ArgumentNullException>(() => SqlServerJobsServiceCollectionExtensions.AddRogueSqlServerJobs(null!, ConnectionString));
    }

    [Fact]
    public void BatchRows_CarryEachJobsRunTime_OrNullForNow()
    {
        var at = new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.FromHours(2));
        NewJob[] jobs =
        {
            new NewJob { Id = Guid.NewGuid(), Kind = "k", Queue = "q", Payload = new byte[] { 1 }, MaxAttempts = 1, RunAt = at },
            new NewJob { Id = Guid.NewGuid(), Kind = "k", Queue = "q", Payload = new byte[] { 2 }, MaxAttempts = 1 },
        };

        var rows = (System.Collections.Generic.IEnumerable<Microsoft.Data.SqlClient.Server.SqlDataRecord>)SqlServerRows.Jobs("@jobs", "[s].[job_batch]", jobs).Value;
        (bool IsNull, DateTime RunAt)[] runAts = rows.Select(static r => (r.IsDBNull(6), r.IsDBNull(6) ? default : r.GetDateTime(6))).ToArray();

        Assert.Equal((false, at.UtcDateTime), runAts[0]);
        Assert.True(runAts[1].IsNull);
    }

    [Fact]
    public void PollInterval_DropsToTheMin_OnAFind_AndDoublesUpToTheMax_Otherwise()
    {
        TimeSpan min = TimeSpan.FromMilliseconds(100);
        TimeSpan max = TimeSpan.FromMilliseconds(1000);

        TimeSpan[] idle = Enumerable.Range(0, 6)
            .Aggregate(new[] { min }, (seen, _) => seen.Append(SqlServerJobPoller.NextInterval(seen[^1], false, min, max)).ToArray());

        Assert.Equal(new[] { 100, 200, 400, 800, 1000, 1000, 1000 }, idle.Select(static t => (int)t.TotalMilliseconds));
        Assert.Equal(min, SqlServerJobPoller.NextInterval(max, true, min, max));
        Assert.Equal(min, SqlServerJobPoller.NextInterval(TimeSpan.Zero, false, min, max));
        Assert.Equal(max, SqlServerJobPoller.NextInterval(max - TimeSpan.FromTicks(1), false, min, max));
        Assert.Equal(min, SqlServerJobPoller.NextInterval(min, false, min, min));
    }
}
