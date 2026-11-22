using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Jobs.Postgres.Tests;

/// <summary>How completions are joined into statements, with the database write replaced by a gate.</summary>
public sealed class CompletionBatcherTests
{
    private static readonly JobLease _a = new JobLease(Guid.NewGuid(), 1);
    private static readonly JobLease _b = new JobLease(Guid.NewGuid(), 1);
    private static readonly JobLease _c = new JobLease(Guid.NewGuid(), 2);

    [Fact]
    public async Task ALoneCompletion_IsWrittenAtOnce()
    {
        var writes = new List<IReadOnlyList<JobLease>>();
        var batcher = new CompletionBatcher(leases =>
        {
            writes.Add(leases);
            return Task.FromResult(new HashSet<JobLease>(leases));
        });

        int count = await batcher.CompleteAsync(new[] { _a }, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(new[] { _a }, Assert.Single(writes));
    }

    [Fact]
    public async Task CompletionsArrivingDuringAWrite_AreWrittenTogether_InTheNextOne()
    {
        var writes = new List<IReadOnlyList<JobLease>>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batcher = new CompletionBatcher(async leases =>
        {
            writes.Add(leases);
            if (writes.Count == 1)
            {
                writing.SetResult();
                await release.Task;
            }
            return new HashSet<JobLease>(leases);
        });

        Task<int> first = batcher.CompleteAsync(new[] { _a }, CancellationToken.None).AsTask();
        await writing.Task;
        Task<int> second = batcher.CompleteAsync(new[] { _b }, CancellationToken.None).AsTask();
        Task<int> third = batcher.CompleteAsync(new[] { _c, _b }, CancellationToken.None).AsTask();
        release.SetResult();

        // _b is named twice in the second write; it counts once, for the caller that named it first.
        Assert.Equal(new[] { 1, 1, 1 }, await Task.WhenAll(first, second, third));
        Assert.Equal(2, writes.Count);
        Assert.Equal(new[] { _b, _c, _b }, writes[1]);
    }

    [Fact]
    public async Task OnlyLeasesTheStoreStillHeld_AreCounted()
    {
        var batcher = new CompletionBatcher(static _ => Task.FromResult(new HashSet<JobLease> { _b }));

        Assert.Equal(1, await batcher.CompleteAsync(new[] { _a, _b, _c }, CancellationToken.None));
    }

    [Fact]
    public async Task AFailedWrite_FailsEveryCallerInIt_AndTheNextCompletionIsWrittenAgain()
    {
        int calls = 0;
        var batcher = new CompletionBatcher(leases => ++calls == 1
            ? Task.FromException<HashSet<JobLease>>(new InvalidOperationException("down"))
            : Task.FromResult(new HashSet<JobLease>(leases)));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await batcher.CompleteAsync(new[] { _a }, CancellationToken.None));

        Assert.Equal(1, await batcher.CompleteAsync(new[] { _a }, CancellationToken.None));
    }
}
