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
/// uses queues of its own, so a database store may share one database across the suite.
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

    [Fact]
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

    [Fact]
    public async Task Claim_TakesNothing_FromAnEmptyOrAnotherQueue()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        Assert.Empty(await store.ClaimAsync(Queue, 10, _lease, None));

        await store.EnqueueAsync(new[] { Job(Queue + "-other") }, None);

        Assert.Empty(await store.ClaimAsync(Queue, 10, _lease, None));
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task Retry_WithNoDelay_IsDueAtOnce_AndANegativeDelayIsRejected()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        await store.EnqueueAsync(new[] { Job(Queue) }, None);
        ClaimedJob claimed = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.RetryAsync(claimed.Lease, TimeSpan.FromTicks(-1), "e", None));
        Assert.True(await store.RetryAsync(claimed.Lease, TimeSpan.Zero, "e", None));
        Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task Release_PutsTheJobBack_WithoutCountingTheAttempt()
    {
        IJobStore store = await CreateStoreAsync(Clock);
        NewJob job = Job(Queue, maxAttempts: 1);
        await store.EnqueueAsync(new[] { job }, None);
        ClaimedJob claimed = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        Assert.True(await store.ReleaseAsync(claimed.Lease, None));
        ClaimedJob again = Assert.Single(await store.ClaimAsync(Queue, 1, _lease, None));

        Assert.Equal(1, again.Attempt);
        Assert.False(await store.ReleaseAsync(claimed.Lease with { Attempt = 5 }, None));
    }

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task UnknownId_HasNoSnapshot()
    {
        IJobStore store = await CreateStoreAsync(Clock);

        Assert.Null(await store.GetAsync(Guid.NewGuid(), None));
    }

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task Enqueue_RejectsANullBatch_AndAcceptsAnEmptyOne()
    {
        IJobStore store = await CreateStoreAsync(Clock);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.EnqueueAsync(null!, None));
        await store.EnqueueAsync(Array.Empty<NewJob>(), None);

        Assert.Empty(await store.ClaimAsync(Queue, 1, _lease, None));
    }

    private static NewJob Job(string queue, string payload = "{}", int maxAttempts = 5)
        => new NewJob { Id = Guid.NewGuid(), Kind = "contract.job", Queue = queue, Payload = Encoding.UTF8.GetBytes(payload), MaxAttempts = maxAttempts };
}
