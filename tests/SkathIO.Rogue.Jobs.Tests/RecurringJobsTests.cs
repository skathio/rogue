using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SkathIO.Rogue.Jobs.StoreContract;
using Xunit;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>Recurring jobs, delayed jobs and unique keys, through the registry, the scheduler and the hosted recurring scheduler.</summary>
public sealed class RecurringJobsTests
{
    // Guards against a hang only; no assertion depends on how long anything takes.
    private static readonly TimeSpan _hang = TimeSpan.FromSeconds(30);

    private static JobDefinition Marked<T>()
        where T : notnull
        => new JobDefinition<T>(typeof(T).Name, new JobDefaults(null, 0, 0, 0), static (_, _, _) => default);

    [Fact]
    public void RecurringJob_OfACommandWithoutJob_FailsStartup_NamingBothAttributes()
    {
        var definitions = new[] { new JobDefinition<UnmarkedJob>("unmarked", JobDefaults.None, static (_, _, _) => default) };
        var recurring = new RecurringJob[] { new RecurringJob<UnmarkedJob>("30 * * * *", static () => new UnmarkedJob(1)) };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new JobRegistry(definitions, new JobsOptions { SerializerContext = TestJobsJson.Default }, recurring));

        Assert.Contains("[Job]", error.Message, StringComparison.Ordinal);
        Assert.Contains("[Recurring]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecurringJob_WithNoWayToCreateItsCommand_FailsStartup_NamingIt()
    {
        var recurring = new RecurringJob[] { new RecurringJob<RecordValue>("30 * * * *", null) };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new JobRegistry(new[] { Marked<RecordValue>() }, new JobsOptions { SerializerContext = TestJobsJson.Default }, recurring));

        Assert.Contains(typeof(RecordValue).ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains("parameterless", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecurringJob_RegisteredTwiceForOneCommand_FailsStartup()
    {
        var recurring = new RecurringJob[]
        {
            new RecurringJob<RecordValue>("30 * * * *", static () => new RecordValue(1)),
            new RecurringJob<RecordValue>("0 * * * *", static () => new RecordValue(2)),
        };

        Assert.Throws<InvalidOperationException>(() => new JobRegistry(new[] { Marked<RecordValue>() }, new JobsOptions { SerializerContext = TestJobsJson.Default }, recurring));
    }

    [Fact]
    public void RecurringJob_IsBound_AndCreatesEachRunKeyedByItsKindAndTime()
    {
        var recurring = new RecurringJob[] { new RecurringJob<RecordValue>("30 * * * *", static () => new RecordValue(7)) };
        var registry = new JobRegistry(new[] { Marked<RecordValue>() }, new JobsOptions { SerializerContext = TestJobsJson.Default }, recurring);
        DateTimeOffset at = new DateTimeOffset(2026, 1, 1, 0, 30, 0, TimeSpan.Zero);

        NewJob run = Assert.Single(registry.Recurring).Create(at);

        Assert.Equal((nameof(RecordValue), nameof(RecordValue), (DateTimeOffset?)at), (run.Kind, run.TaskKey, run.RunAt));
    }

    [Fact]
    public void LeaderLease_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddRogueJobs(static o => o.LeaderLeaseDuration = TimeSpan.Zero));

        new ServiceCollection().AddRogueJobs(static o => o.LeaderLeaseDuration = TimeSpan.FromTicks(1));
    }

    [Fact]
    public void AddRecurringJob_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddRecurringJob(null!));
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddRecurringJob(new RecurringJob<RecordValue>("* * * * *", null)));
    }

    [Fact]
    public async Task UniqueKey_EnqueueMany_GivesEveryCommandThePendingJobsId_AndRunsOnce()
    {
        await using JobsHost host = JobsHost.Create();

        IReadOnlyList<Guid> ids = await host.Scheduler.EnqueueMany(new[] { new UniqueJob(1), new UniqueJob(2), new UniqueJob(3) });
        Guid scheduled = await host.Scheduler.Schedule(new UniqueJob(4), host.Clock.GetUtcNow().AddHours(1));
        await host.Worker.RunOnceAsync();
        Guid afterwards = await host.Scheduler.Enqueue(new UniqueJob(5));
        await host.Worker.RunOnceAsync();

        Assert.Single(ids.Distinct());
        Assert.Equal(ids[0], scheduled);
        Assert.NotEqual(ids[0], afterwards);
        Assert.Equal(new[] { "unique:1", "unique:5" }, host.Probe.Ran);
    }

    [Fact]
    public async Task Schedule_AtATimeAlreadyPast_IsDueAtOnce()
    {
        await using JobsHost host = JobsHost.Create();

        Guid id = await host.Scheduler.Schedule(new RecordValue(3), host.Clock.GetUtcNow().AddDays(-1));
        int ran = await host.Worker.RunOnceAsync();

        Assert.Equal(1, ran);
        Assert.Equal(JobState.Succeeded, (await host.SnapshotAsync(id)).State);
    }

    [Fact]
    public async Task RecurringScheduler_WhenTheLeaderStops_AnotherHostTakesOver()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryJobStore(clock);
        await using JobsHost first = Host(clock, store);
        await using JobsHost second = Host(clock, store);
        IHostedService leader = Scheduler(first);
        IHostedService follower = Scheduler(second);

        await leader.StartAsync(CancellationToken.None);
        await follower.StartAsync(CancellationToken.None);
        await leader.StopAsync(CancellationToken.None).WaitAsync(_hang);
        await DriveAsync(clock, second, untilRuns: 2, cap: clock.GetUtcNow().AddHours(2));
        await follower.StopAsync(CancellationToken.None).WaitAsync(_hang);

        Assert.Equal(new[] { "value:30", "value:30" }, second.Probe.Ran);
        Assert.Empty(first.Probe.Ran);
    }

    private static JobsHost Host(ManualTimeProvider clock, InMemoryJobStore store) => JobsHost.Create(services: s =>
    {
        s.AddSingleton<TimeProvider>(clock);
        s.AddSingleton<IJobStore>(store);
        s.AddRecurringJob(new RecurringJob<RecordValue>("30 * * * *", static () => new RecordValue(30)));
        s.AddRogueJobWorker();
    });

    private static IHostedService Scheduler(JobsHost host) => host.Services.GetServices<IHostedService>().Single(static h => h is RecurringJobService);

    // Moves the clock in small steps, running due jobs on the host after each, until it has run the
    // given number or the clock reaches the cap; yields between steps so the hosted scheduler sees each.
    private static async Task DriveAsync(ManualTimeProvider clock, JobsHost host, int untilRuns, DateTimeOffset cap)
    {
        using var guard = new CancellationTokenSource(_hang);
        while (host.Probe.Ran.Count < untilRuns && clock.GetUtcNow() < cap)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(1, guard.Token);
            await host.Worker.RunOnceAsync();
        }
    }
}
