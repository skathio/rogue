using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue.Jobs;
using Xunit;

namespace SkathIO.Rogue.Consumer.Jobs.Tests;

/// <summary>
/// The jobs an app runs, from the packed packages, on the store each derived class registers: enqueue,
/// delayed, recurring, unique, retry and dead-letter, each run by a <see cref="JobWorker"/>. The clock
/// moves only when a test moves it, so every outcome is the same on every run.
/// </summary>
public abstract class JobsScenarioTests
{
    // Guards against a hang only; no assertion depends on how long anything takes.
    private static readonly TimeSpan _hang = TimeSpan.FromMinutes(2);

    /// <summary>Registers the store under test, as the app's startup does.</summary>
    protected abstract void AddStore(IServiceCollection services);

    private JobsHost CreateHost(bool hostedWorker = false) => JobsHost.Create(AddStore, hostedWorker);

    [JobsFact]
    public async Task Enqueue_AndEnqueueMany_StoreJobs_ThatTheWorkerRuns()
    {
        await using JobsHost host = CreateHost();

        Guid one = await host.Scheduler.Enqueue(new ShipParcel(1));
        IReadOnlyList<Guid> many = await host.Scheduler.EnqueueMany(new[] { new ShipParcel(2), new ShipParcel(3), new ShipParcel(4) });
        int ran = await host.Worker.RunOnceAsync();

        Assert.Equal(4, ran);
        Assert.Equal(4, many.Prepend(one).Distinct().Count());
        foreach (Guid id in many.Prepend(one))
        {
            Assert.Equal(JobState.Succeeded, (await host.SnapshotAsync(id)).State);
        }
        Assert.Equal(new[] { "shipped 1", "shipped 2", "shipped 3", "shipped 4" }, host.Probe.Ran());
        Assert.Equal(0, await host.Worker.RunOnceAsync());
    }

    [JobsFact]
    public async Task Schedule_HoldsTheJob_UntilItsTime_ThenTheWorkerRunsIt()
    {
        await using JobsHost host = CreateHost();
        DateTimeOffset at = JobsHost.Start.AddHours(1);

        Guid id = await host.Scheduler.Schedule(new ShipParcel(7), at);
        int early = await host.Worker.RunOnceAsync();
        host.Clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
        int justBefore = await host.Worker.RunOnceAsync();
        JobSnapshot waiting = await host.SnapshotAsync(id);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        int onTime = await host.Worker.RunOnceAsync();

        Assert.Equal((0, 0, 1), (early, justBefore, onTime));
        Assert.Equal((JobState.Enqueued, at), (waiting.State, waiting.RunAt));
        Assert.Equal(JobState.Succeeded, (await host.SnapshotAsync(id)).State);
        Assert.Equal(new[] { "shipped 7" }, host.Probe.Ran());
    }

    [JobsFact]
    public async Task Recurring_TheHostedSchedulerStoresTheNextOccurrence_AndTheHostedWorkerRunsItWhenDue()
    {
        await using JobsHost host = CreateHost(hostedWorker: true);
        using var guard = new CancellationTokenSource(_hang);

        await host.StartAsync(guard.Token);
        StoredOccurrence occurrence = await host.Watched.FirstOccurrence.WaitAsync(guard.Token);
        JobSnapshot stored = await host.SnapshotAsync(occurrence.Id);
        host.Clock.Advance(TimeSpan.FromMinutes(30));
        await host.Probe.Pruned.WaitAsync(guard.Token);
        await host.StopAsync(guard.Token);

        Assert.Equal((typeof(PruneCarts).FullName, JobsHost.Start.AddMinutes(30)), (occurrence.Kind, occurrence.RunAt));
        Assert.Equal((JobState.Enqueued, occurrence.Kind), (stored.State, stored.Kind));
        Assert.Equal(JobState.Succeeded, (await host.SnapshotAsync(occurrence.Id)).State);
        Assert.Equal(new[] { PruneCartsHandler.Entry }, host.Probe.Ran());
    }

    [JobsFact]
    public async Task UniqueKey_WhileAJobIsPending_EveryEnqueueGetsItsId_AndOnceItRanTheNextIsStored()
    {
        await using JobsHost host = CreateHost();

        Guid first = await host.Scheduler.Enqueue(new RebuildSearchIndex(1));
        Guid second = await host.Scheduler.Enqueue(new RebuildSearchIndex(2));
        IReadOnlyList<Guid> many = await host.Scheduler.EnqueueMany(new[] { new RebuildSearchIndex(3), new RebuildSearchIndex(4) });
        int ran = await host.Worker.RunOnceAsync();
        Guid afterwards = await host.Scheduler.Enqueue(new RebuildSearchIndex(5));
        int ranAgain = await host.Worker.RunOnceAsync();

        Assert.Equal(new[] { first, first, first }, new[] { second, many[0], many[1] });
        Assert.NotEqual(first, afterwards);
        Assert.Equal((1, 1), (ran, ranAgain));
        Assert.Equal(new[] { "rebuilt for 1", "rebuilt for 5" }, host.Probe.Ran());
    }

    [JobsFact]
    public async Task AFailedAttempt_IsRetriedAfterItsBackoff_AndTheRetrySucceeds()
    {
        await using JobsHost host = CreateHost();
        host.Probe.DeclinesLeft["4242"] = 1;

        Guid id = await host.Scheduler.Enqueue(new ChargeCard("4242"));
        int firstAttempt = await host.Worker.RunOnceAsync();
        JobSnapshot afterFailure = await host.SnapshotAsync(id);
        host.Clock.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond));
        int beforeBackoff = await host.Worker.RunOnceAsync();
        host.Clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond));
        int retry = await host.Worker.RunOnceAsync();
        JobSnapshot afterRetry = await host.SnapshotAsync(id);

        Assert.Equal((1, 0, 1), (firstAttempt, beforeBackoff, retry));
        Assert.Equal((JobState.Enqueued, 1, JobsHost.Start.AddSeconds(2)), (afterFailure.State, afterFailure.Attempt, afterFailure.RunAt));
        Assert.Contains(ChargeCardHandler.Declined, afterFailure.LastError, StringComparison.Ordinal);
        Assert.Equal((JobState.Succeeded, 2), (afterRetry.State, afterRetry.Attempt));
        Assert.Equal(new[] { "charged 4242", "declined 4242" }, host.Probe.Ran());
    }

    [JobsFact]
    public async Task AJobThatFailsItsLastAttempt_IsDeadLettered_AndNeverRunsAgain()
    {
        await using JobsHost host = CreateHost();

        Guid id = await host.Scheduler.Enqueue(new CallPartner(9));
        int firstAttempt = await host.Worker.RunOnceAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        int lastAttempt = await host.Worker.RunOnceAsync();
        JobSnapshot deadLettered = await host.SnapshotAsync(id);
        host.Clock.Advance(TimeSpan.FromDays(1));
        int later = await host.Worker.RunOnceAsync();

        Assert.Equal((1, 1, 0), (firstAttempt, lastAttempt, later));
        Assert.Equal((JobState.DeadLettered, 2, 2), (deadLettered.State, deadLettered.Attempt, deadLettered.MaxAttempts));
        Assert.Contains(CallPartnerHandler.Down, deadLettered.LastError, StringComparison.Ordinal);
        Assert.Equal(JobState.DeadLettered, (await host.SnapshotAsync(id)).State);
        Assert.Equal(new[] { "called 9", "called 9" }, host.Probe.Ran());
    }
}
