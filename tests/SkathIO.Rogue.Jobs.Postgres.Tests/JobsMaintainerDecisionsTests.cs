using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SkathIO.Rogue.Jobs.Stores;
using SkathIO.Rogue.Jobs.StoreContract;
using Xunit;

namespace SkathIO.Rogue.Jobs.Postgres.Tests;

/// <summary>
/// The maintainer's decisions on the jobs code: the worker runs only in a host that asks for it, only
/// commands marked <c>[Job]</c> run as jobs, stored errors carry no NUL, a kind this process does not
/// know is put back for later instead of dead-lettered, dead-lettered jobs can be requeued, and a
/// Postgres schema name with a trailing newline is rejected.
/// </summary>
public sealed class JobsMaintainerDecisionsTests
{
    private const string NulMessage = "bad\0news";

    // Longer than any backoff the worker applies: retries are capped at JobsOptions.MaxBackoff (one hour by default).
    private static readonly TimeSpan _pastAnyBackoff = TimeSpan.FromDays(1);

    private static CancellationToken None => CancellationToken.None;

    [Fact]
    public void AddRogueJobs_RegistersEnqueueing_ButNoHostedWorker()
    {
        using ServiceProvider provider = Host(new ManualTimeProvider(Start)).BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IJobScheduler>());
        Assert.NotNull(provider.GetRequiredService<IJobStore>());
        Assert.Empty(provider.GetServices<IHostedService>());
    }

    [Fact]
    public void PostgresHost_WithoutTheWorkerCall_HasNoHostedWorker()
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=localhost");
        ServiceCollection services = Host(new ManualTimeProvider(Start));
        services.AddRoguePostgresJobs(dataSource);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.All(provider.GetServices<IHostedService>(), static s => Assert.True(
            s is PostgresJobListener || s is JobPruner,
            s.GetType() + " is hosted by an enqueue-only Postgres host."));
    }

    [Fact]
    public async Task StoredRowNamingACommandWithoutJobAttribute_IsNeverDispatched()
    {
        var clock = new ManualTimeProvider(Start);
        await using ServiceProvider provider = Host(clock).BuildServiceProvider();
        IJobStore store = provider.GetRequiredService<IJobStore>();
        var row = new NewJob
        {
            Id = Guid.NewGuid(),
            Kind = typeof(UnmarkedCommand).FullName!,
            Queue = "default",
            Payload = Encoding.UTF8.GetBytes("{\"Id\":7}"),
            MaxAttempts = 3,
        };

        await store.EnqueueAsync(new[] { row }, None);
        for (int round = 0; round < 3; round++)
        {
            await Worker(provider).RunOnceAsync();
            clock.Advance(_pastAnyBackoff);
        }

        Assert.Empty(provider.GetRequiredService<UnmarkedSeen>().Ids);
        Assert.NotEqual(JobState.Succeeded, (await store.GetAsync(row.Id, None))!.State);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("\0", "")]
    [InlineData("\0\0\0", "")]
    [InlineData("a\0b", "ab")]
    [InlineData("\0x\0", "x")]
    [InlineData("no nul here", "no nul here")]
    public void TrimError_StripsEveryNul(string error, string expected)
    {
        Assert.Equal(expected, JobLimits.TrimError(error));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(JobLimits.MaxErrorLength - 1)]
    [InlineData(JobLimits.MaxErrorLength)]
    [InlineData(JobLimits.MaxErrorLength + 1)]
    public void TrimError_LeavesNoNul_AndStaysWithinTheLimit_WhereverTheNulSits(int position)
    {
        string error = new string('x', position) + "\0" + new string('y', JobLimits.MaxErrorLength);

        string trimmed = JobLimits.TrimError(error);

        Assert.DoesNotContain('\0', trimmed);
        Assert.True(trimmed.Length <= JobLimits.MaxErrorLength, "Trimmed error is " + trimmed.Length + " characters long.");
    }

    [Fact]
    public async Task FailureWhoseMessageHasANul_IsRecordedWithoutIt_OnRetryAndDeadLetter()
    {
        var clock = new ManualTimeProvider(Start);
        await using ServiceProvider provider = Host(clock, failWithNul: true, maxAttempts: 2).BuildServiceProvider();
        IJobStore store = provider.GetRequiredService<IJobStore>();

        Guid id = await provider.GetRequiredService<IJobScheduler>().Enqueue(new PostgresTestJob("nul"));
        await Worker(provider).RunOnceAsync();
        JobSnapshot retried = (await store.GetAsync(id, None))!;
        clock.Advance(_pastAnyBackoff);
        await Worker(provider).RunOnceAsync();
        JobSnapshot deadLettered = (await store.GetAsync(id, None))!;

        Assert.Equal(JobState.Enqueued, retried.State);
        Assert.Equal(JobState.DeadLettered, deadLettered.State);
        Assert.All(new[] { retried.LastError, deadLettered.LastError }, static error =>
        {
            Assert.NotNull(error);
            Assert.DoesNotContain('\0', error);
            Assert.Contains("badnews", error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task UnknownKind_IsPutBackForLater_NotDeadLettered()
    {
        var clock = new ManualTimeProvider(Start);
        await using ServiceProvider provider = Host(clock).BuildServiceProvider();
        IJobStore store = provider.GetRequiredService<IJobStore>();
        var row = new NewJob
        {
            Id = Guid.NewGuid(),
            Kind = "deploy.newer-kind",
            Queue = "default",
            Payload = Encoding.UTF8.GetBytes("{}"),
            MaxAttempts = 1,
        };
        await store.EnqueueAsync(new[] { row }, None);

        int firstRound = await Worker(provider).RunOnceAsync();
        JobSnapshot afterFirst = (await store.GetAsync(row.Id, None))!;
        int rightAfter = await Worker(provider).RunOnceAsync();
        var later = new List<JobState>();
        for (int round = 0; round < 3; round++)
        {
            clock.Advance(_pastAnyBackoff);
            await Worker(provider).RunOnceAsync();
            later.Add((await store.GetAsync(row.Id, None))!.State);
        }

        Assert.Equal(1, firstRound);
        Assert.Equal(JobState.Enqueued, afterFirst.State);
        Assert.Equal(0, rightAfter);
        Assert.All(later, static state => Assert.Equal(JobState.Enqueued, state));
    }

    [Fact]
    public async Task RollingDeploy_JobMetByAWorkerThatDoesNotKnowItsKind_RunsOnceANewerWorkerDoes()
    {
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryJobStore(clock);

        // No AddRogue: the generated job definitions are never registered, so this worker knows no kind.
        var oldServices = new ServiceCollection();
        oldServices.AddSingleton<TimeProvider>(clock);
        oldServices.AddSingleton<IJobStore>(store);
        oldServices.AddRogueJobs(static o => o.SerializerContext = DecisionsJson.Default);
        await using ServiceProvider oldHost = oldServices.BuildServiceProvider();

        ServiceCollection newServices = Host(clock);
        newServices.AddSingleton<IJobStore>(store);
        await using ServiceProvider newHost = newServices.BuildServiceProvider();

        Guid id = await newHost.GetRequiredService<IJobScheduler>().Enqueue(new PostgresTestJob("survives"));
        int metByOld = await Worker(oldHost).RunOnceAsync();
        JobState afterOld = (await store.GetAsync(id, None))!.State;
        clock.Advance(_pastAnyBackoff);
        await Worker(newHost).RunOnceAsync();

        Assert.Equal(1, metByOld);
        Assert.Equal(JobState.Enqueued, afterOld);
        Assert.Equal(new[] { "survives" }, newHost.GetRequiredService<Ran>().Values);
        Assert.Equal(JobState.Succeeded, (await store.GetAsync(id, None))!.State);
    }

    [Fact]
    public async Task InMemoryStore_RequeuesDeadLetteredJobs()
    {
        var clock = new ManualTimeProvider(Start);

        await RequeueChecks.RunAsync(new InMemoryJobStore(clock), "default");
    }

    [Theory]
    [InlineData("a\n")]
    [InlineData("rogue_jobs\n")]
    [InlineData("a1234567890123456789012345678901234567890123456789\n")]
    public void Schema_WithATrailingNewline_IsRejected(string schema)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=localhost");

        Assert.Throws<ArgumentException>(() => new PostgresJobStore(dataSource, new PostgresJobsOptions { Schema = schema }));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddRoguePostgresJobs(dataSource, o => o.Schema = schema));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a1234567890123456789012345678901234567890123456789")]
    public void Schema_WithoutTheNewline_IsStillAccepted(string schema)
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=localhost");

        Assert.Equal(schema, new PostgresJobStore(dataSource, new PostgresJobsOptions { Schema = schema }).Options.Schema);
    }

    internal static DateTimeOffset Start => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The services an application registers to enqueue jobs: AddRogue and AddRogueJobs, no worker call.
    /// With <paramref name="failWithNul"/> the test job's handler cannot be built, failing with a message
    /// that holds a NUL.
    /// </summary>
    internal static ServiceCollection Host(ManualTimeProvider clock, bool failWithNul = false, int maxAttempts = 5)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(new UnmarkedSeen());
        if (failWithNul)
        {
            services.AddScoped<Ran>(static _ => throw new InvalidOperationException(NulMessage));
        }
        else
        {
            services.AddSingleton(new Ran());
        }
        services.AddRogue();
        services.AddRogueJobs(o =>
        {
            o.SerializerContext = DecisionsJson.Default;
            o.BackoffJitter = 0;
            o.DefaultMaxAttempts = maxAttempts;
        });
        return services;
    }

    // The worker type stays resolvable for hosts that drain jobs themselves through RunOnceAsync.
    internal static JobWorker Worker(IServiceProvider provider) => provider.GetRequiredService<JobWorker>();
}

/// <summary>The Postgres store under the same decisions: requeue, and a NUL-bearing failure that still stores.</summary>
[Collection(PostgresCollection.Name)]
[RequiresDocker]
public sealed class JobsMaintainerDecisionsPostgresTests
{
    private readonly PostgresFixture _database;

    public JobsMaintainerDecisionsPostgresTests(PostgresFixture database) => _database = database;

    [StoreFact]
    public async Task PostgresStore_RequeuesDeadLetteredJobs()
    {
        var clock = new ManualTimeProvider(JobsMaintainerDecisionsTests.Start);

        await RequeueChecks.RunAsync(new PostgresJobStore(_database.DataSource, new PostgresJobsOptions { Schema = PostgresFixture.NewSchema() }, clock), "q");
    }

    [StoreFact]
    public async Task PostgresStore_StoresTheRetryAndDeadLetter_OfAFailureWhoseMessageHasANul()
    {
        var clock = new ManualTimeProvider(JobsMaintainerDecisionsTests.Start);
        ServiceCollection services = JobsMaintainerDecisionsTests.Host(clock, failWithNul: true, maxAttempts: 2);
        string schema = PostgresFixture.NewSchema();
        services.AddRoguePostgresJobs(_database.DataSource, o => o.Schema = schema);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IJobStore store = provider.GetRequiredService<IJobStore>();

        Guid id = await provider.GetRequiredService<IJobScheduler>().Enqueue(new PostgresTestJob("nul"));
        await JobsMaintainerDecisionsTests.Worker(provider).RunOnceAsync();
        JobSnapshot retried = (await store.GetAsync(id, CancellationToken.None))!;
        clock.Advance(TimeSpan.FromDays(1));
        await JobsMaintainerDecisionsTests.Worker(provider).RunOnceAsync();
        JobSnapshot deadLettered = (await store.GetAsync(id, CancellationToken.None))!;

        Assert.IsType<PostgresJobStore>(store);
        Assert.Equal((JobState.Enqueued, 1), (retried.State, retried.Attempt));
        Assert.Equal(JobState.DeadLettered, deadLettered.State);
        Assert.Contains("badnews", deadLettered.LastError, StringComparison.Ordinal);
    }
}

/// <summary>Requeue through the store contract: none, one and several dead-lettered jobs.</summary>
internal static class RequeueChecks
{
    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(1);

    public static async Task RunAsync(IJobStore store, string queue)
    {
        CancellationToken none = CancellationToken.None;
        Guid[] dead = await DeadLetterAsync(store, queue, 3);

        await RequeueAsync(store, Array.Empty<Guid>());
        JobState[] afterNone = await StatesAsync(store, dead);
        await RequeueAsync(store, new[] { dead[0] });
        JobState[] afterOne = await StatesAsync(store, dead);
        await RequeueAsync(store, new[] { dead[1], dead[2] });
        JobState[] afterTwo = await StatesAsync(store, dead);
        IReadOnlyList<ClaimedJob> claimed = await store.ClaimAsync(queue, 10, _lease, none);

        Assert.All(afterNone, static s => Assert.Equal(JobState.DeadLettered, s));
        Assert.Equal(new[] { JobState.Enqueued, JobState.DeadLettered, JobState.DeadLettered }, afterOne);
        Assert.All(afterTwo, static s => Assert.Equal(JobState.Enqueued, s));
        Assert.Equal(dead.OrderBy(static g => g), claimed.Select(static c => c.Id).OrderBy(static g => g));
    }

    // Bound late so this project still builds, and every other test still runs, before the store
    // contract gains its requeue member; until then this fails with a missing-member error.
    private static async Task RequeueAsync(IJobStore store, Guid[] ids)
        => await ((dynamic)store).RequeueAsync(ids, CancellationToken.None);

    private static async Task<Guid[]> DeadLetterAsync(IJobStore store, string queue, int count)
    {
        NewJob[] jobs = Enumerable.Range(0, count).Select(_ => new NewJob
        {
            Id = Guid.NewGuid(),
            Kind = "decisions.requeue",
            Queue = queue,
            Payload = Encoding.UTF8.GetBytes("{}"),
            MaxAttempts = 1,
        }).ToArray();
        await store.EnqueueAsync(jobs, CancellationToken.None);
        IReadOnlyList<ClaimedJob> claimed = await store.ClaimAsync(queue, count, _lease, CancellationToken.None);
        foreach (ClaimedJob job in claimed)
        {
            Assert.True(await store.DeadLetterAsync(job.Lease, "failed", CancellationToken.None));
        }
        Assert.Equal(count, claimed.Count);
        return jobs.Select(static j => j.Id).ToArray();
    }

    private static async Task<JobState[]> StatesAsync(IJobStore store, Guid[] ids)
    {
        var states = new JobState[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            states[i] = (await store.GetAsync(ids[i], CancellationToken.None))!.State;
        }
        return states;
    }
}

/// <summary>What the handler of the unmarked command saw.</summary>
public sealed class UnmarkedSeen
{
    public ConcurrentQueue<int> Ids { get; } = new ConcurrentQueue<int>();
}

/// <summary>A command with a handler but no <c>[Job]</c>: never a runnable job kind.</summary>
public sealed record UnmarkedCommand(int Id) : ICommand;

public sealed class UnmarkedCommandHandler : ICommandHandler<UnmarkedCommand>
{
    private readonly UnmarkedSeen _seen;

    public UnmarkedCommandHandler(UnmarkedSeen seen) => _seen = seen;

    public ValueTask Handle(UnmarkedCommand command, CancellationToken cancellationToken)
    {
        _seen.Ids.Enqueue(command.Id);
        return default;
    }
}

/// <summary>Covers the test job and the unmarked command, so only the missing attribute keeps the latter from running.</summary>
[JsonSerializable(typeof(PostgresTestJob))]
[JsonSerializable(typeof(UnmarkedCommand))]
public sealed partial class DecisionsJson : JsonSerializerContext
{
}
