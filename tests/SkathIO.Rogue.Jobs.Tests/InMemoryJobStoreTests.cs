using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Jobs.StoreContract;
using Xunit;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>The in-memory store against the shared store contract.</summary>
public sealed class InMemoryJobStoreContractTests : JobStoreContract
{
    protected override Task<IJobStore> CreateStoreAsync(TimeProvider clock) => Task.FromResult<IJobStore>(new InMemoryJobStore(clock));
}

/// <summary>What only the in-memory store does: bounded retention of finished jobs.</summary>
public sealed class InMemoryJobStoreTests
{
    [Fact]
    public async Task FinishedJobs_PastTheRetention_AreDroppedOldestFirst()
    {
        var store = new InMemoryJobStore(TimeProvider.System, finishedRetention: 1);
        NewJob first = Job();
        NewJob second = Job();
        await store.EnqueueAsync(new[] { first, second }, CancellationToken.None);
        var claimed = await store.ClaimAsync("q", 2, TimeSpan.FromMinutes(1), CancellationToken.None);

        await store.CompleteAsync(new[] { claimed[0].Lease }, CancellationToken.None);
        JobSnapshot? kept = await store.GetAsync(first.Id, CancellationToken.None);
        await store.CompleteAsync(new[] { claimed[1].Lease }, CancellationToken.None);

        Assert.NotNull(kept);
        Assert.Null(await store.GetAsync(first.Id, CancellationToken.None));
        Assert.Equal(JobState.Succeeded, (await store.GetAsync(second.Id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task HandledMessageIds_PastTheRetention_AreDroppedOldestFirst()
    {
        var store = new InMemoryJobStore(TimeProvider.System, finishedRetention: 1);
        await store.CompleteMessageAsync("first", 0, CancellationToken.None);
        MessageClaim kept = (await store.TryClaimMessageAsync("first", TimeSpan.FromMinutes(1), CancellationToken.None)).Outcome;

        await store.CompleteMessageAsync("second", 0, CancellationToken.None);

        Assert.Equal(MessageClaim.Handled, kept);
        Assert.Equal(MessageClaim.Claimed, (await store.TryClaimMessageAsync("first", TimeSpan.FromMinutes(1), CancellationToken.None)).Outcome);
        Assert.Equal(MessageClaim.Handled, (await store.TryClaimMessageAsync("second", TimeSpan.FromMinutes(1), CancellationToken.None)).Outcome);
    }

    [Fact]
    public void Retention_RejectsANegativeValue_AndAcceptsZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryJobStore(TimeProvider.System, -1));
        Assert.Throws<ArgumentNullException>(() => new InMemoryJobStore(null!));
        _ = new InMemoryJobStore(TimeProvider.System, 0);
    }

    private static NewJob Job() => new NewJob { Id = Guid.NewGuid(), Kind = "k", Queue = "q", Payload = Encoding.UTF8.GetBytes("{}"), MaxAttempts = 1 };
}
