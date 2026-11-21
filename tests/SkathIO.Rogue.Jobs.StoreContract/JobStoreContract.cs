using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Jobs.StoreContract;

/// <summary>
/// The behaviour every <see cref="IJobStore"/> must show, written once and run against each store: a
/// store's test project derives from this class and creates its store on the given clock. Every test
/// uses queues of its own, so a database store may share one database across the suite. A store whose
/// database runs in a container marks its class [RequiresDocker], so the suite skips without Docker.
/// </summary>
public abstract class JobStoreContract
{
    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(1);

    /// <summary>The clock the store runs on; tests move it forward by hand.</summary>
    protected ManualTimeProvider Clock { get; } = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>A queue name no other test uses.</summary>
    protected string Queue { get; } = "q-" + Guid.NewGuid().ToString("N").Substring(0, 12);

    /// <summary>Creates the store under test on <paramref name="clock"/>.</summary>
    protected abstract Task<IJobStore> CreateStoreAsync(TimeProvider clock);

    private static CancellationToken None => CancellationToken.None;

    [StoreFact]
    public async Task EnqueuedJob_IsClaimedOnce_AsAttemptOne_WithItsPayload()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob job = Job(Queue, "payload");
        await store.EnqueueAsync(new[] { job }, None);

        IReadOnlyList<ClaimedJob> claimed = await store.ClaimAsync(Queue, 10, _lease, None);
        IReadOnlyList<ClaimedJob> again = await store.ClaimAsync(Queue, 10, _lease, None);

        ClaimedJob only = Assert.Single(claimed);
        Assert.Equal(job.Id, only.Id);
        Assert.Equal(job.Kind, only.Kind);
        Assert.Equal(Queue, only.Queue);
        Assert.Equal("payload", Encoding.UTF8.GetString(only.Payload.Span));
        Assert.Equal(1, only.Attempt);
        Assert.Equal(job.MaxAttempts, only.MaxAttempts);
        Assert.Empty(again);
        Assert.Equal(JobState.Processing, (await store.GetAsync(job.Id, None))!.State);
    }

    [StoreFact]
    public async Task Claim_TakesNothing_FromAnEmptyOrAnotherQueue()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        Assert.Empty(await store.ClaimAsync(Queue, 10, _lease, None));

        await store.EnqueueAsync(new[] { Job(Queue + "-other") }, None);

        Assert.Empty(await store.ClaimAsync(Queue, 10, _lease, None));
    }

    [StoreFact]
    public async Task Claim_TakesAtMostMaxCount_InEnqueueOrder()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob[] jobs = Enumerable.Range(0, 5).Select(i => Job(Queue, "job" + i)).ToArray();
        await store.EnqueueAsync(jobs, None);

        IReadOnlyList<ClaimedJob> first = await store.ClaimAsync(Queue, 2, _lease, None);
        IReadOnlyList<ClaimedJob> rest = await store.ClaimAsync(Queue, 10, _lease, None);

        Assert.Equal(jobs.Take(2).Select(static j => j.Id), first.Select(static j => j.Id));
        Assert.Equal(jobs.Skip(2).Select(static j => j.Id), rest.Select(static j => j.Id));
    }

    [StoreFact]
    public async Task ConcurrentClaims_NeverReturnTheSameJob()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob[] jobs = Enumerable.Range(0, 60).Select(_ => Job(Queue)).ToArray();
        await store.EnqueueAsync(jobs, None);

        IReadOnlyList<ClaimedJob>[] claims = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () => await store.ClaimAsync(Queue, 4, _lease, None))));

        Guid[] claimedIds = claims.SelectMany(static c => c).Select(static j => j.Id).ToArray();
        Assert.Equal(claimedIds.Length, claimedIds.Distinct().Count());
        Assert.Equal(jobs.Select(static j => j.Id).OrderBy(static id => id), claimedIds.OrderBy(static id => id));
    }

    [StoreFact]
    public async Task Complete_MarksTheJobSucceeded_OnlyForTheClaimThatHoldsIt()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob job = Job(Queue);
        await store.EnqueueAsync(new[] { job }, None);
        ClaimedJob claimed = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        int stale = await store.CompleteAsync(new[] { new JobLease(job.Id, claimed.Attempt + 1), new JobLease(Guid.NewGuid(), 1) }, None);
        int held = await store.CompleteAsync(new[] { claimed.Lease }, None);
        int twice = await store.CompleteAsync(new[] { claimed.Lease }, None);

        Assert.Equal(0, stale);
        Assert.Equal(1, held);
        Assert.Equal(0, twice);
        Assert.Equal(JobState.Succeeded, (await store.GetAsync(job.Id, None))!.State);
        Assert.Empty(await store.ClaimAsync(Queue, 10, _lease, None));
    }

    [StoreFact]
    public async Task Retry_WaitsOutItsDelay_CountsTheNextAttempt_AndRecordsTheError()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob job = Job(Queue);
        await store.EnqueueAsync(new[] { job }, None);
        ClaimedJob first = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        Assert.True(await store.RetryAsync(first.Lease, TimeSpan.FromSeconds(30), "boom", None));
        JobSnapshot waiting = (await store.GetAsync(job.Id, None))!;
        Clock.Advance(TimeSpan.FromSeconds(29));
        IReadOnlyList<ClaimedJob> early = await store.ClaimAsync(Queue, 1, _lease, None);
        Clock.Advance(TimeSpan.FromSeconds(1));
        ClaimedJob second = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        Assert.Equal(JobState.Enqueued, waiting.State);
        Assert.Equal("boom", waiting.LastError);
        Assert.Equal(1, waiting.Attempt);
        Assert.Empty(early);
        Assert.Equal(2, second.Attempt);
        Assert.False(await store.RetryAsync(first.Lease, TimeSpan.Zero, "stale", None));
    }

    [StoreFact]
    public async Task Retry_WithNoDelay_IsDueAtOnce_AndANegativeDelayIsRejected()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        await store.EnqueueAsync(new[] { Job(Queue) }, None);
        ClaimedJob claimed = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.RetryAsync(claimed.Lease, TimeSpan.FromTicks(-1), "e", None));
        Assert.True(await store.RetryAsync(claimed.Lease, TimeSpan.Zero, "e", None));
        Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));
    }

    [StoreFact]
    public async Task DeadLetteredJob_IsNeverClaimedAgain()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob job = Job(Queue);
        await store.EnqueueAsync(new[] { job }, None);
        ClaimedJob claimed = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        Assert.True(await store.DeadLetterAsync(claimed.Lease, "poison", None));
        Clock.Advance(TimeSpan.FromDays(1));

        Assert.Empty(await store.ClaimAsync(Queue, 10, _lease, None));
        JobSnapshot snapshot = (await store.GetAsync(job.Id, None))!;
        Assert.Equal(JobState.DeadLettered, snapshot.State);
        Assert.Equal("poison", snapshot.LastError);
        Assert.False(await store.DeadLetterAsync(claimed.Lease, "again", None));
    }

    [StoreFact]
    public async Task ExpiredLease_IsClaimedAgain_AndTheEarlierClaimNoLongerCounts()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob job = Job(Queue, maxAttempts: 3);
        await store.EnqueueAsync(new[] { job }, None);
        ClaimedJob first = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        Clock.Advance(_lease - TimeSpan.FromTicks(1));
        IReadOnlyList<ClaimedJob> stillHeld = await store.ClaimAsync(Queue, 1, _lease, None);
        Clock.Advance(TimeSpan.FromTicks(1));
        ClaimedJob second = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        Assert.Empty(stillHeld);
        Assert.Equal(2, second.Attempt);
        Assert.Equal(0, await store.CompleteAsync(new[] { first.Lease }, None));
        Assert.False(await store.RetryAsync(first.Lease, TimeSpan.Zero, "late", None));
        Assert.Equal(1, await store.CompleteAsync(new[] { second.Lease }, None));
    }

    [StoreFact]
    public async Task ExpiredLease_OnTheLastAttempt_DeadLettersTheJob()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob job = Job(Queue, maxAttempts: 1);
        await store.EnqueueAsync(new[] { job }, None);
        Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        Clock.Advance(_lease);

        Assert.Empty(await store.ClaimAsync(Queue, 1, _lease, None));
        JobSnapshot snapshot = (await store.GetAsync(job.Id, None))!;
        Assert.Equal(JobState.DeadLettered, snapshot.State);
        Assert.False(string.IsNullOrEmpty(snapshot.LastError));
    }

    [StoreFact]
    public async Task ExtendedLease_KeepsTheJobFromBeingClaimedAgain()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob job = Job(Queue);
        await store.EnqueueAsync(new[] { job }, None);
        ClaimedJob claimed = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        Clock.Advance(TimeSpan.FromSeconds(50));
        int extended = await store.ExtendLeasesAsync(new[] { claimed.Lease, new JobLease(Guid.NewGuid(), 1) }, _lease, None);
        Clock.Advance(TimeSpan.FromSeconds(50));

        Assert.Equal(1, extended);
        Assert.Empty(await store.ClaimAsync(Queue, 1, _lease, None));
        Assert.Equal(1, await store.CompleteAsync(new[] { claimed.Lease }, None));
    }

    [StoreFact]
    public async Task Release_PutsTheJobBack_WithoutCountingTheAttempt()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob job = Job(Queue, maxAttempts: 1);
        await store.EnqueueAsync(new[] { job }, None);
        ClaimedJob claimed = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        Assert.True(await store.ReleaseAsync(claimed.Lease, TimeSpan.Zero, None));
        ClaimedJob again = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        Assert.Equal(1, again.Attempt);
        Assert.False(await store.ReleaseAsync(claimed.Lease with { Attempt = 5 }, TimeSpan.Zero, None));
    }

    [StoreFact]
    public async Task Release_WithADelay_WaitsItOut_AndANegativeDelayIsRejected()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob job = Job(Queue, maxAttempts: 1);
        await store.EnqueueAsync(new[] { job }, None);
        ClaimedJob claimed = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.ReleaseAsync(claimed.Lease, TimeSpan.FromTicks(-1), None));
        Assert.True(await store.ReleaseAsync(claimed.Lease, TimeSpan.FromSeconds(30), None));
        JobSnapshot waiting = (await store.GetAsync(job.Id, None))!;
        Clock.Advance(TimeSpan.FromSeconds(29));
        IReadOnlyList<ClaimedJob> early = await store.ClaimAsync(Queue, 1, _lease, None);
        Clock.Advance(TimeSpan.FromSeconds(1));
        ClaimedJob again = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        Assert.Equal((JobState.Enqueued, 0), (waiting.State, waiting.Attempt));
        Assert.Empty(early);
        Assert.Equal(1, again.Attempt);
    }

    [StoreFact]
    public async Task Requeue_MovesOnlyDeadLetteredJobs_WithAllTheirAttempts()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob dead = Job(Queue, maxAttempts: 2);
        NewJob waiting = Job(Queue);
        await store.EnqueueAsync(new[] { dead }, None);
        ClaimedJob claimed = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));
        Assert.True(await store.DeadLetterAsync(claimed.Lease, "poison", None));
        await store.EnqueueAsync(new[] { waiting }, None);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.RequeueAsync(null!, None));
        Assert.Equal(0, await store.RequeueAsync(Array.Empty<Guid>(), None));
        int moved = await store.RequeueAsync(new[] { dead.Id, waiting.Id, Guid.NewGuid() }, None);
        int movedAgain = await store.RequeueAsync(new[] { dead.Id }, None);
        JobSnapshot requeued = (await store.GetAsync(dead.Id, None))!;
        IReadOnlyList<ClaimedJob> claimable = await store.ClaimAsync(Queue, 10, _lease, None);

        Assert.Equal(1, moved);
        Assert.Equal(0, movedAgain);
        Assert.Equal((JobState.Enqueued, 0), (requeued.State, requeued.Attempt));
        Assert.Equal(new[] { 1, 1 }, claimable.Select(static c => c.Attempt));
        Assert.Equal(new[] { dead.Id, waiting.Id }.OrderBy(static g => g), claimable.Select(static c => c.Id).OrderBy(static g => g));
    }

    [StoreFact]
    public async Task RecordedError_HasNoNul_OnRetryAndDeadLetter()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob job = Job(Queue);
        await store.EnqueueAsync(new[] { job }, None);
        ClaimedJob first = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        Assert.True(await store.RetryAsync(first.Lease, TimeSpan.Zero, "re\0try", None));
        string? retried = (await store.GetAsync(job.Id, None))!.LastError;
        ClaimedJob second = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));
        Assert.True(await store.DeadLetterAsync(second.Lease, "\0dead\0", None));
        string? deadLettered = (await store.GetAsync(job.Id, None))!.LastError;

        Assert.Equal("retry", retried);
        Assert.Equal("dead", deadLettered);
    }

    [StoreFact]
    public async Task LargeBatch_IsStoredWhole_AndClaimable()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob[] jobs = Enumerable.Range(0, 1000).Select(_ => Job(Queue)).ToArray();

        await store.EnqueueAsync(jobs, None);
        int claimed = 0;
        for (IReadOnlyList<ClaimedJob> batch; (batch = await store.ClaimAsync(Queue, 300, _lease, None)).Count > 0;)
        {
            claimed += batch.Count;
        }

        Assert.Equal(1000, claimed);
    }

    [StoreFact]
    public async Task DuplicateId_FailsTheWholeBatch()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob stored = Job(Queue);
        await store.EnqueueAsync(new[] { stored }, None);
        NewJob fresh = Job(Queue);
        NewJob repeat = new NewJob { Id = stored.Id, Kind = stored.Kind, Queue = Queue, Payload = stored.Payload, MaxAttempts = 1 };

        await Assert.ThrowsAnyAsync<Exception>(async () => await store.EnqueueAsync(new[] { fresh, repeat }, None));
        NewJob twin = Job(Queue);
        await Assert.ThrowsAnyAsync<Exception>(async () => await store.EnqueueAsync(new[] { twin, twin }, None));

        Assert.Null(await store.GetAsync(fresh.Id, None));
        Assert.Null(await store.GetAsync(twin.Id, None));
    }

    [StoreFact]
    public async Task UnknownId_HasNoSnapshot()
    {
        IJobStore store = await CreateStoreAsync(Clock);

        Assert.Null(await store.GetAsync(Guid.NewGuid(), None));
    }

    [StoreFact]
    public async Task RecordedError_IsCutToTheLimit()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        await store.EnqueueAsync(new[] { Job(Queue), Job(Queue) }, None);
        IReadOnlyList<ClaimedJob> claimed = await store.ClaimAsync(Queue, 2, _lease, None);

        await store.RetryAsync(claimed[0].Lease, TimeSpan.Zero, new string('x', JobLimits.MaxErrorLength), None);
        await store.DeadLetterAsync(claimed[1].Lease, new string('y', JobLimits.MaxErrorLength + 1), None);

        Assert.Equal(JobLimits.MaxErrorLength, (await store.GetAsync(claimed[0].Id, None))!.LastError!.Length);
        Assert.Equal(new string('y', JobLimits.MaxErrorLength), (await store.GetAsync(claimed[1].Id, None))!.LastError);
    }

    [StoreFact]
    public async Task Claim_RejectsAnOutOfRangeArgument_AndAcceptsTheSmallestValid()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        await store.EnqueueAsync(new[] { Job(Queue) }, None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.ClaimAsync(Queue, 0, _lease, None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.ClaimAsync(Queue, 1, TimeSpan.Zero, None));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await store.ClaimAsync(string.Empty, 1, _lease, None));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await store.ClaimAsync(null!, 1, _lease, None));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await store.ClaimAsync(new string('q', JobLimits.MaxQueueLength + 1), 1, _lease, None));

        Assert.Single(await store.ClaimAsync(Queue, 1, TimeSpan.FromTicks(1), None));
    }

    [StoreFact]
    public async Task Enqueue_RejectsANullBatch_AndAcceptsAnEmptyOne()
    {
        IJobStore store = await CreateStoreAsync(Clock);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.EnqueueAsync(null!, None));
        await store.EnqueueAsync(Array.Empty<NewJob>(), None);

        Assert.Empty(await store.ClaimAsync(Queue, 1, _lease, None));
    }

    [StoreFact]
    public async Task DelayedJob_IsClaimedAtItsRunAt_AndNotBefore()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        DateTimeOffset at = Clock.GetUtcNow().AddHours(1);
        NewJob now = Job(Queue);
        NewJob later = Keyed(Queue, runAt: at);
        NewJob latest = Keyed(Queue, runAt: at.AddHours(1));
        await store.EnqueueAsync(new[] { latest, now, later }, None);

        IReadOnlyList<ClaimedJob> first = await store.ClaimAsync(Queue, 10, _lease, None);
        Assert.Equal(first.Count, await store.CompleteAsync(first.Select(static c => c.Lease).ToArray(), None));
        JobSnapshot waiting = (await store.GetAsync(later.Id, None))!;
        Clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromTicks(1));
        IReadOnlyList<ClaimedJob> early = await store.ClaimAsync(Queue, 10, _lease, None);
        Clock.Advance(TimeSpan.FromTicks(1));
        IReadOnlyList<ClaimedJob> due = await store.ClaimAsync(Queue, 10, _lease, None);

        Assert.Equal(new[] { now.Id }, first.Select(static c => c.Id));
        Assert.Equal((JobState.Enqueued, at), (waiting.State, waiting.RunAt));
        Assert.Empty(early);
        Assert.Equal(new[] { later.Id }, due.Select(static c => c.Id));
    }

    [StoreFact]
    public async Task Batch_WithAKeyedJob_IsRejected_AndStoresNothing()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob plain = Job(Queue);

        await Assert.ThrowsAsync<ArgumentException>(async () => await store.EnqueueAsync(new[] { plain, Keyed(Queue, uniqueKey: "k") }, None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.EnqueueAsync(new[] { Keyed(Queue, taskKey: "t", runAt: Clock.GetUtcNow().AddMinutes(1)) }, None));

        Assert.Null(await store.GetAsync(plain.Id, None));
    }

    [StoreFact]
    public async Task UniqueKey_WhilePending_GivesThePendingJob_AndOnceItSucceeds_ANewOne()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob first = Keyed(Queue, uniqueKey: "k");

        Guid stored = await store.EnqueueOnceAsync(first, None);
        NewJob whileEnqueued = Keyed(Queue, uniqueKey: "k");
        Guid duplicate = await store.EnqueueOnceAsync(whileEnqueued, None);
        ClaimedJob claimed = Assert.Single(await store.ClaimAsync(Queue, 10, _lease, None));
        Guid whileProcessing = await store.EnqueueOnceAsync(Keyed(Queue, uniqueKey: "k"), None);
        NewJob sameKey = Keyed(Queue, uniqueKey: "k");
        var otherKind = new NewJob { Id = Guid.NewGuid(), Kind = "contract.other", Queue = Queue, Payload = sameKey.Payload, MaxAttempts = 1, UniqueKey = sameKey.UniqueKey };
        Guid ofOtherKind = await store.EnqueueOnceAsync(otherKind, None);
        Assert.Equal(1, await store.CompleteAsync(new[] { claimed.Lease }, None));
        NewJob afterwards = Keyed(Queue, uniqueKey: "k");
        Guid next = await store.EnqueueOnceAsync(afterwards, None);

        Assert.Equal(first.Id, stored);
        Assert.Equal(first.Id, duplicate);
        Assert.Null(await store.GetAsync(whileEnqueued.Id, None));
        Assert.Equal(first.Id, whileProcessing);
        Assert.Equal(otherKind.Id, ofOtherKind);
        Assert.Equal(afterwards.Id, next);
    }

    [StoreFact]
    public async Task UniqueKey_IsFreed_WhenItsJobIsDeadLettered_AndHeldThroughARetry()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob first = Keyed(Queue, uniqueKey: "k");
        await store.EnqueueOnceAsync(first, None);

        ClaimedJob claimed = Assert.Single(await store.ClaimAsync(Queue, 10, _lease, None));
        Assert.True(await store.RetryAsync(claimed.Lease, TimeSpan.Zero, "again", None));
        Guid duringRetry = await store.EnqueueOnceAsync(Keyed(Queue, uniqueKey: "k"), None);
        ClaimedJob retried = Assert.Single(await store.ClaimAsync(Queue, 10, _lease, None));
        Assert.True(await store.DeadLetterAsync(retried.Lease, "poison", None));
        NewJob afterwards = Keyed(Queue, uniqueKey: "k");
        Guid next = await store.EnqueueOnceAsync(afterwards, None);

        Assert.Equal(first.Id, duringRetry);
        Assert.Equal(afterwards.Id, next);
    }

    [StoreFact]
    public async Task TaskKey_StoresOneJobPerRunTime()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        DateTimeOffset at = Clock.GetUtcNow().AddMinutes(30);
        NewJob run = Keyed(Queue, taskKey: "task", runAt: at);
        NewJob again = Keyed(Queue, taskKey: "task", runAt: at);
        NewJob nextRun = Keyed(Queue, taskKey: "task", runAt: at.AddHours(1));
        NewJob otherTask = Keyed(Queue, taskKey: "other", runAt: at);

        Guid first = await store.EnqueueOnceAsync(run, None);
        Guid second = await store.EnqueueOnceAsync(again, None);
        Guid third = await store.EnqueueOnceAsync(nextRun, None);
        Guid fourth = await store.EnqueueOnceAsync(otherTask, None);

        Assert.Equal((run.Id, run.Id, nextRun.Id, otherTask.Id), (first, second, third, fourth));
        Assert.Null(await store.GetAsync(again.Id, None));
        Assert.Equal(at, (await store.GetAsync(run.Id, None))!.RunAt);
    }

    [StoreFact]
    public async Task TaskKey_WhoseUniqueKeyIsHeld_GivesTheHolder_ForThatRunFromThenOn()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        DateTimeOffset at = Clock.GetUtcNow().AddMinutes(30);
        NewJob holder = Keyed(Queue, uniqueKey: "k");
        await store.EnqueueOnceAsync(holder, None);

        Guid run = await store.EnqueueOnceAsync(Keyed(Queue, uniqueKey: "k", taskKey: "task", runAt: at), None);
        Guid runAgain = await store.EnqueueOnceAsync(Keyed(Queue, taskKey: "task", runAt: at), None);

        Assert.Equal(holder.Id, run);
        Assert.Equal(holder.Id, runAgain);
    }

    [StoreFact]
    public async Task EnqueueOnce_WithNoKey_StoresTheJob_AndRejectsNull()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob plain = Job(Queue);

        Assert.Equal(plain.Id, await store.EnqueueOnceAsync(plain, None));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.EnqueueOnceAsync(null!, None));
        Assert.Single(await store.ClaimAsync(Queue, 10, _lease, None));
    }

    [StoreFact]
    public async Task Requeue_SkipsAJob_WhoseUniqueKeyIsHeld_AndMovesOneOfTwoThatShareIt()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob first = await DeadLetteredAsync(store, Keyed(Queue, uniqueKey: "k"));
        NewJob second = await DeadLetteredAsync(store, Keyed(Queue, uniqueKey: "k"));
        NewJob holder = Keyed(Queue, uniqueKey: "k");
        await store.EnqueueOnceAsync(holder, None);

        int whileHeld = await store.RequeueAsync(new[] { first.Id, second.Id }, None);
        ClaimedJob held = Assert.Single(await store.ClaimAsync(Queue, 10, _lease, None));
        Assert.Equal(1, await store.CompleteAsync(new[] { held.Lease }, None));
        int once = await store.RequeueAsync(new[] { first.Id, second.Id }, None);

        Assert.Equal(0, whileHeld);
        Assert.Equal(1, once);
        Assert.Single(await store.ClaimAsync(Queue, 10, _lease, None));
    }

    [StoreFact]
    public async Task Leadership_IsHeldByOneOwner_UntilReleasedOrItsLeaseRunsOut()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        string name = "lead-" + Queue;

        bool first = await store.TryAcquireLeadershipAsync(name, "a", _lease, None);
        bool rival = await store.TryAcquireLeadershipAsync(name, "b", _lease, None);

        // The clock moves in whole microseconds, the finest time every store keeps (Postgres timestamptz
        // keeps no finer), so "just before the lease ends" is a time each store can tell apart from its end.
        TimeSpan justUnder = _lease - TimeSpan.FromMicroseconds(1);
        Clock.Advance(justUnder);
        bool renewed = await store.TryAcquireLeadershipAsync(name, "a", _lease, None);
        Clock.Advance(justUnder);
        bool rivalBeforeExpiry = await store.TryAcquireLeadershipAsync(name, "b", _lease, None);
        Clock.Advance(TimeSpan.FromMicroseconds(1));
        bool rivalAfterExpiry = await store.TryAcquireLeadershipAsync(name, "b", _lease, None);
        bool formerLeader = await store.TryAcquireLeadershipAsync(name, "a", _lease, None);
        await store.ReleaseLeadershipAsync(name, "a", None);
        bool stillB = await store.TryAcquireLeadershipAsync(name, "a", _lease, None);
        await store.ReleaseLeadershipAsync(name, "b", None);
        bool afterRelease = await store.TryAcquireLeadershipAsync(name, "a", _lease, None);
        bool otherLock = await store.TryAcquireLeadershipAsync(name + "-x", "b", _lease, None);

        Assert.Equal((true, false, true), (first, rival, renewed));
        Assert.Equal((false, true, false), (rivalBeforeExpiry, rivalAfterExpiry, formerLeader));
        Assert.Equal((false, true, true), (stillB, afterRelease, otherLock));
    }

    [StoreFact]
    public async Task Leadership_RejectsAnInvalidName_Owner_OrLease()
    {
        IJobStore store = await CreateStoreAsync(Clock);

        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await store.TryAcquireLeadershipAsync(string.Empty, "a", _lease, None));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await store.TryAcquireLeadershipAsync("lead", "a b", _lease, None));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await store.TryAcquireLeadershipAsync(new string('n', JobLimits.MaxKeyLength + 1), "a", _lease, None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.TryAcquireLeadershipAsync("lead", "a", TimeSpan.Zero, None));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await store.ReleaseLeadershipAsync("lead", null!, None));

        Assert.True(await store.TryAcquireLeadershipAsync("lead-" + Queue, new string('o', JobLimits.MaxKeyLength), TimeSpan.FromTicks(1), None));
    }

    private async Task<NewJob> DeadLetteredAsync(IJobStore store, NewJob job)
    {
        Assert.Equal(job.Id, await store.EnqueueOnceAsync(job, None));
        ClaimedJob claimed = Assert.Single(await store.ClaimAsync(Queue, 10, _lease, None));
        Assert.True(await store.DeadLetterAsync(claimed.Lease, "poison", None));
        return job;
    }

    private static NewJob Job(string queue, string payload = "{}", int maxAttempts = 5)
        => new NewJob { Id = Guid.NewGuid(), Kind = "contract.job", Queue = queue, Payload = Encoding.UTF8.GetBytes(payload), MaxAttempts = maxAttempts };

    // Keys are suffixed with the test's own queue, so tests sharing a database never share a key.
    private static NewJob Keyed(string queue, string? uniqueKey = null, string? taskKey = null, DateTimeOffset? runAt = null)
        => new NewJob
        {
            Id = Guid.NewGuid(),
            Kind = "contract.job",
            Queue = queue,
            Payload = Encoding.UTF8.GetBytes("{}"),
            MaxAttempts = 5,
            RunAt = runAt,
            UniqueKey = uniqueKey is null ? null : uniqueKey + "-" + queue,
            TaskKey = taskKey is null ? null : taskKey + "-" + queue,
        };
}
