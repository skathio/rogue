using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>
/// The worker running jobs through the code the generator emitted for this project: success, retries
/// with exponential backoff, dead-lettering, timeouts, shutdown, lease renewal and bounded concurrency.
/// </summary>
public sealed class JobWorkerTests
{
    private static readonly TimeSpan _hangGuard = TimeSpan.FromSeconds(30);

    [Fact]
    public void GeneratedDefinitions_CoverEveryCommand_AndNoQuery()
    {
        using ServiceProvider services = JobsHost.Create().Services;

        string[] kinds = services.GetServices<JobDefinition>().Select(static d => d.Kind).OrderBy(static k => k, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[]
        {
            "SkathIO.Rogue.Jobs.Tests.FlakyJob",
            "SkathIO.Rogue.Jobs.Tests.GateJob",
            "SkathIO.Rogue.Jobs.Tests.RecordValue",
            "SkathIO.Rogue.Jobs.Tests.SlowJob",
            "SkathIO.Rogue.Jobs.Tests.TelemetryJob",
            "SkathIO.Rogue.Jobs.Tests.UnmarkedJob",
            "tests.priced",
        }, kinds);
    }

    [Fact]
    public void GeneratedDefinitions_CarryTheJobAttributeDefaults()
    {
        using ServiceProvider services = JobsHost.Create().Services;
        Dictionary<string, JobDefinition> byKind = services.GetServices<JobDefinition>().ToDictionary(static d => d.Kind);

        JobDefaults flaky = byKind["SkathIO.Rogue.Jobs.Tests.FlakyJob"].Defaults;
        JobDefaults priced = byKind["tests.priced"].Defaults;
        JobDefaults unmarked = byKind["SkathIO.Rogue.Jobs.Tests.UnmarkedJob"].Defaults;

        Assert.Equal((null, 3, 2, 0), (flaky.Queue, flaky.MaxAttempts, flaky.BackoffSeconds, flaky.TimeoutSeconds));
        Assert.Equal("billing", priced.Queue);
        Assert.Same(JobDefaults.None, unmarked);
        Assert.Equal(typeof(PriceJob), byKind["tests.priced"].CommandType);
    }

    [Fact]
    public async Task EnqueuedCommand_RunsItsHandler_AndSucceeds()
    {
        await using JobsHost host = JobsHost.Create();

        Guid id = await host.Scheduler.Enqueue(new RecordValue(7));
        int ran = await host.Worker.RunOnceAsync();

        Assert.Equal(1, ran);
        Assert.Equal(new[] { "value:7" }, host.Probe.Ran);
        JobSnapshot snapshot = await host.SnapshotAsync(id);
        Assert.Equal(JobState.Succeeded, snapshot.State);
        Assert.Equal(1, snapshot.Attempt);
    }

    [Fact]
    public async Task ResultCommand_RunsUnderItsOwnKindAndQueue()
    {
        await using JobsHost host = JobsHost.Create();

        Guid id = await host.Scheduler.Enqueue(new PriceJob(5));
        JobSnapshot queued = await host.SnapshotAsync(id);
        await host.Worker.RunOnceAsync();

        Assert.Equal("tests.priced", queued.Kind);
        Assert.Equal("billing", queued.Queue);
        Assert.Equal(new[] { "price:5" }, host.Probe.Ran);
        Assert.Equal(JobState.Succeeded, (await host.SnapshotAsync(id)).State);
    }

    [Fact]
    public async Task CommandWithoutJobAttribute_IsNotAJob_EvenThoughTheContextCoversIt()
    {
        await using JobsHost host = JobsHost.Create();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await host.Scheduler.Enqueue(new UnmarkedJob(3)));

        Assert.Contains("[Job]", error.Message, StringComparison.Ordinal);
        Assert.Empty(host.Probe.Ran);
    }

    [Fact]
    public async Task EachJob_RunsInAServiceScopeOfItsOwn()
    {
        await using JobsHost host = JobsHost.Create();

        await host.Scheduler.EnqueueMany(new[] { new RecordValue(1), new RecordValue(2) });
        await host.Worker.RunOnceAsync();

        Assert.Equal(2, host.Probe.ScopeIds.Distinct().Count());
    }

    [Fact]
    public async Task FailedRun_IsRetriedAfterADoublingBackoff_UntilItSucceeds()
    {
        await using JobsHost host = JobsHost.Create();
        host.Probe.FailuresLeft["a"] = 2;
        Guid id = await host.Scheduler.Enqueue(new FlakyJob("a"));

        Assert.Equal(1, await host.Worker.RunOnceAsync());
        JobSnapshot afterFirst = await host.SnapshotAsync(id);
        host.Clock.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1));
        Assert.Equal(0, await host.Worker.RunOnceAsync());
        host.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, await host.Worker.RunOnceAsync());

        host.Clock.Advance(TimeSpan.FromSeconds(4) - TimeSpan.FromTicks(1));
        Assert.Equal(0, await host.Worker.RunOnceAsync());
        host.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, await host.Worker.RunOnceAsync());

        Assert.Equal(JobState.Enqueued, afterFirst.State);
        Assert.Contains("flaky failure for a", afterFirst.LastError, StringComparison.Ordinal);
        JobSnapshot done = await host.SnapshotAsync(id);
        Assert.Equal(JobState.Succeeded, done.State);
        Assert.Equal(3, done.Attempt);
    }

    [Fact]
    public async Task RetryDelay_IsCappedAtMaxBackoff()
    {
        await using JobsHost host = JobsHost.Create(static o =>
        {
            o.DefaultBackoff = TimeSpan.FromSeconds(1);
            o.MaxBackoff = TimeSpan.FromSeconds(3);
        });
        host.Probe.FailuresLeft["c"] = 2;
        Guid id = await host.Scheduler.Enqueue(new FlakyJob("c"));

        await host.Worker.RunOnceAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        await host.Worker.RunOnceAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(1, await host.Worker.RunOnceAsync());
        Assert.Equal(JobState.Succeeded, (await host.SnapshotAsync(id)).State);
    }

    [Fact]
    public async Task FailureOnTheLastAttempt_DeadLettersTheJob()
    {
        await using JobsHost host = JobsHost.Create();
        host.Probe.FailuresLeft["b"] = 100;
        Guid id = await host.Scheduler.Enqueue(new FlakyJob("b"));

        await host.Worker.RunOnceAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        await host.Worker.RunOnceAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(4));
        await host.Worker.RunOnceAsync();
        host.Clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(0, await host.Worker.RunOnceAsync());
        JobSnapshot snapshot = await host.SnapshotAsync(id);
        Assert.Equal(JobState.DeadLettered, snapshot.State);
        Assert.Equal(3, snapshot.Attempt);
        Assert.Contains("InvalidOperationException", snapshot.LastError, StringComparison.Ordinal);
        Assert.Equal(3, host.Probe.Ran.Count);
    }

    [Fact]
    public async Task RunPastItsTimeout_IsCancelled_AndCountsAsAFailure()
    {
        await using JobsHost host = JobsHost.Create();
        Guid id = await host.Scheduler.Enqueue(new SlowJob(1));

        Task<int> running = host.Worker.RunOnceAsync();
        await host.Probe.Started.Task.WaitAsync(_hangGuard);
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        await running.WaitAsync(_hangGuard);

        JobSnapshot snapshot = await host.SnapshotAsync(id);
        Assert.Equal(JobState.Enqueued, snapshot.State);
        Assert.Contains("TimeoutException", snapshot.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shutdown_PutsTheRunningJobBack_WithoutCountingTheAttempt()
    {
        await using JobsHost host = JobsHost.Create();
        Guid id = await host.Scheduler.Enqueue(new GateJob(1));
        ClaimedJob claimed = Assert.Single(await host.Store.ClaimAsync("default", 1, TimeSpan.FromMinutes(5), CancellationToken.None));
        using var stopping = new CancellationTokenSource();

        Task<string> running = host.Worker.ExecuteAsync(claimed, stopping.Token);
        await host.Probe.Started.Task.WaitAsync(_hangGuard);
        await stopping.CancelAsync();

        Assert.Equal("released", await running.WaitAsync(_hangGuard));
        JobSnapshot snapshot = await host.SnapshotAsync(id);
        Assert.Equal(JobState.Enqueued, snapshot.State);
        Assert.Equal(0, snapshot.Attempt);
    }

    [Fact]
    public async Task UnknownKind_IsPutBackAfterTheDefaultBackoff_WithoutCountingTheAttempt()
    {
        await using JobsHost host = JobsHost.Create(static o => o.DefaultBackoff = TimeSpan.FromSeconds(30));
        var job = new NewJob { Id = Guid.NewGuid(), Kind = "nobody.knows", Queue = "default", Payload = Encoding.UTF8.GetBytes("{}"), MaxAttempts = 5 };
        await host.Store.EnqueueAsync(new[] { job }, CancellationToken.None);

        await host.Worker.RunOnceAsync();

        JobSnapshot snapshot = await host.SnapshotAsync(job.Id);
        Assert.Equal((JobState.Enqueued, 0), (snapshot.State, snapshot.Attempt));
        Assert.Equal(host.Clock.GetUtcNow() + TimeSpan.FromSeconds(30), snapshot.RunAt);
    }

    [Fact]
    public async Task UnknownKind_WithNoBackoff_IsPutBackForAPollInterval_NotAtOnce()
    {
        await using JobsHost host = JobsHost.Create(static o =>
        {
            o.DefaultBackoff = TimeSpan.Zero;
            o.PollInterval = TimeSpan.FromSeconds(5);
        });
        var job = new NewJob { Id = Guid.NewGuid(), Kind = "nobody.knows", Queue = "default", Payload = Encoding.UTF8.GetBytes("{}"), MaxAttempts = 5 };
        await host.Store.EnqueueAsync(new[] { job }, CancellationToken.None);

        await host.Worker.RunOnceAsync();

        Assert.Equal(host.Clock.GetUtcNow() + TimeSpan.FromSeconds(5), (await host.SnapshotAsync(job.Id)).RunAt);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    public async Task UnreadablePayload_IsDeadLetteredWithoutARetry(string payload)
    {
        await using JobsHost host = JobsHost.Create();
        var job = new NewJob { Id = Guid.NewGuid(), Kind = "SkathIO.Rogue.Jobs.Tests.RecordValue", Queue = "default", Payload = Encoding.UTF8.GetBytes(payload), MaxAttempts = 5 };
        await host.Store.EnqueueAsync(new[] { job }, CancellationToken.None);

        await host.Worker.RunOnceAsync();

        JobSnapshot snapshot = await host.SnapshotAsync(job.Id);
        Assert.Equal(JobState.DeadLettered, snapshot.State);
        Assert.Equal(1, snapshot.Attempt);
        Assert.Empty(host.Probe.Ran);
    }

    [Fact]
    public async Task RenewedLease_KeepsALongRunningJobItsOwn()
    {
        await using JobsHost host = JobsHost.Create();
        Guid id = await host.Scheduler.Enqueue(new GateJob(1));

        Task<int> running = host.Worker.RunOnceAsync();
        await host.Probe.Started.Task.WaitAsync(_hangGuard);
        host.Clock.Advance(TimeSpan.FromMinutes(4));
        int renewed = await host.Worker.RenewLeasesAsync(CancellationToken.None);
        host.Clock.Advance(TimeSpan.FromMinutes(4));
        IReadOnlyList<ClaimedJob> stolen = await host.Store.ClaimAsync("default", 1, TimeSpan.FromMinutes(5), CancellationToken.None);
        host.Probe.Gate.SetResult();
        await running.WaitAsync(_hangGuard);

        Assert.Equal(1, renewed);
        Assert.Empty(stolen);
        Assert.Equal(JobState.Succeeded, (await host.SnapshotAsync(id)).State);
        Assert.Equal(0, await host.Worker.RenewLeasesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Worker_RunsNoMoreThanMaxConcurrencyJobsAtOnce()
    {
        await using JobsHost host = JobsHost.Create(static o => o.MaxConcurrency = 2);
        host.Probe.StartTarget = 2;
        await host.Scheduler.EnqueueMany(new[] { new GateJob(1), new GateJob(2), new GateJob(3) });

        Task<int> running = host.Worker.RunOnceAsync();
        await host.Probe.Started.Task.WaitAsync(_hangGuard);
        int runningWhileHeld = Volatile.Read(ref host.Probe.Running);
        host.Probe.Gate.SetResult();

        Assert.Equal(3, await running.WaitAsync(_hangGuard));
        Assert.Equal(2, runningWhileHeld);
        Assert.Equal(2, host.Probe.MaxRunning);
        Assert.Equal(3, host.Probe.Ran.Count);
    }

    [Fact]
    public async Task ExplicitQueues_AreTheOnlyOnesProcessed()
    {
        await using JobsHost host = JobsHost.Create(static o => o.Queues.Add("billing"));
        Guid elsewhere = await host.Scheduler.Enqueue(new RecordValue(1));
        Guid billed = await host.Scheduler.Enqueue(new PriceJob(2));

        await host.Worker.RunOnceAsync();

        Assert.Equal(JobState.Enqueued, (await host.SnapshotAsync(elsewhere)).State);
        Assert.Equal(JobState.Succeeded, (await host.SnapshotAsync(billed)).State);
    }

    [Fact]
    public async Task RunAsync_PicksUpAnEnqueuedJob_AndReturnsOnceItHasStopped()
    {
        await using JobsHost host = JobsHost.Create();
        using var stopping = new CancellationTokenSource();
        Task worker = host.Worker.RunAsync(stopping.Token);

        Guid id = await host.Scheduler.Enqueue(new GateJob(1));
        await host.Probe.Started.Task.WaitAsync(_hangGuard);
        host.Probe.Gate.SetResult();
        await host.Probe.Finished.Task.WaitAsync(_hangGuard);
        await stopping.CancelAsync();
        await worker.WaitAsync(_hangGuard);

        Assert.Equal(JobState.Succeeded, (await host.SnapshotAsync(id)).State);
    }

    [Fact]
    public async Task RunAsync_OnShutdown_PutsRunningJobsBack()
    {
        await using JobsHost host = JobsHost.Create();
        using var stopping = new CancellationTokenSource();
        Task worker = host.Worker.RunAsync(stopping.Token);

        Guid id = await host.Scheduler.Enqueue(new GateJob(1));
        await host.Probe.Started.Task.WaitAsync(_hangGuard);
        await stopping.CancelAsync();
        await worker.WaitAsync(_hangGuard);

        JobSnapshot snapshot = await host.SnapshotAsync(id);
        Assert.Equal(JobState.Enqueued, snapshot.State);
        Assert.Equal(0, snapshot.Attempt);
    }
}
