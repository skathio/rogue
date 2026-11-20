using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Jobs.Tests;

/// <summary>Enqueueing: one store call per batch, payload limits, attempt limits and what is not a job.</summary>
public sealed class JobSchedulerTests
{
    [Fact]
    public async Task EnqueueMany_StoresTheBatchInOneCall_AndReturnsIdsInOrder()
    {
        var store = new CountingStore();
        await using JobsHost host = JobsHost.Create(services: s => s.AddSingleton<IJobStore>(store));

        IReadOnlyList<Guid> ids = await host.Scheduler.EnqueueMany(Enumerable.Range(0, 250).Select(static i => new RecordValue(i)).ToArray());

        Assert.Equal(1, store.EnqueueCalls);
        Assert.Equal(250, ids.Distinct().Count());
        Assert.Equal(ids, store.Enqueued.Select(static j => j.Id));
    }

    [Fact]
    public async Task EnqueueMany_OfNothing_StoresNothing()
    {
        var store = new CountingStore();
        await using JobsHost host = JobsHost.Create(services: s => s.AddSingleton<IJobStore>(store));

        IReadOnlyList<Guid> ids = await host.Scheduler.EnqueueMany(Array.Empty<RecordValue>());

        Assert.Empty(ids);
        Assert.Equal(0, store.EnqueueCalls);
    }

    [Fact]
    public async Task EnqueueMany_WithANullCommand_StoresNoneOfThem()
    {
        var store = new CountingStore();
        await using JobsHost host = JobsHost.Create(services: s => s.AddSingleton<IJobStore>(store));

        await Assert.ThrowsAsync<ArgumentException>(async () => await host.Scheduler.EnqueueMany(new[] { new RecordValue(1), null! }));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await host.Scheduler.EnqueueMany<RecordValue>(null!));

        Assert.Equal(0, store.EnqueueCalls);
    }

    [Fact]
    public async Task Enqueue_RejectsANullCommand()
    {
        await using JobsHost host = JobsHost.Create();

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await host.Scheduler.Enqueue<RecordValue>(null!));
    }

    [Fact]
    public async Task Enqueue_OfAQueryOrAnUnknownType_IsRejected()
    {
        await using JobsHost host = JobsHost.Create();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await host.Scheduler.Enqueue(new LookupQuery(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await host.Scheduler.Enqueue("not a command"));
    }

    [Fact]
    public async Task Payload_AtTheLimit_IsAccepted_AndOneBytePast_IsRejected()
    {
        int size = JsonSerializer.SerializeToUtf8Bytes(new RecordValue(7), TestJobsJson.Default.RecordValue).Length;
        await using JobsHost atLimit = JobsHost.Create(o => o.MaxPayloadBytes = size);
        await using JobsHost pastLimit = JobsHost.Create(o => o.MaxPayloadBytes = size - 1);

        await atLimit.Scheduler.Enqueue(new RecordValue(7));
        await Assert.ThrowsAsync<ArgumentException>(async () => await pastLimit.Scheduler.Enqueue(new RecordValue(7)));
        await Assert.ThrowsAsync<ArgumentException>(async () => await pastLimit.Scheduler.EnqueueMany(new[] { new RecordValue(7) }));
    }

    [Fact]
    public async Task EnqueuedJob_TakesItsAttemptLimitFromTheCommand_OrTheOptions()
    {
        await using JobsHost host = JobsHost.Create(static o => o.DefaultMaxAttempts = 7);

        Guid flaky = await host.Scheduler.Enqueue(new FlakyJob("x"));
        Guid plain = await host.Scheduler.Enqueue(new RecordValue(1));

        Assert.Equal(3, (await host.SnapshotAsync(flaky)).MaxAttempts);
        Assert.Equal(7, (await host.SnapshotAsync(plain)).MaxAttempts);
        Assert.Equal("default", (await host.SnapshotAsync(plain)).Queue);
    }

    [Fact]
    public async Task DefaultQueue_IsWhereACommandWithoutAQueueGoes()
    {
        await using JobsHost host = JobsHost.Create(static o => o.DefaultQueue = "general");

        Guid id = await host.Scheduler.Enqueue(new RecordValue(1));
        await host.Worker.RunOnceAsync();

        Assert.Equal("general", (await host.SnapshotAsync(id)).Queue);
        Assert.Equal(JobState.Succeeded, (await host.SnapshotAsync(id)).State);
    }

    private sealed class CountingStore : IJobStore
    {
        private readonly InMemoryJobStore _inner = new InMemoryJobStore();

        public int EnqueueCalls { get; private set; }

        public List<NewJob> Enqueued { get; } = new List<NewJob>();

        public async ValueTask EnqueueAsync(IReadOnlyList<NewJob> jobs, CancellationToken cancellationToken)
        {
            EnqueueCalls++;
            Enqueued.AddRange(jobs);
            await _inner.EnqueueAsync(jobs, cancellationToken);
        }

        public ValueTask<IReadOnlyList<ClaimedJob>> ClaimAsync(string queue, int maxCount, TimeSpan lease, CancellationToken cancellationToken)
            => _inner.ClaimAsync(queue, maxCount, lease, cancellationToken);

        public ValueTask<int> CompleteAsync(IReadOnlyList<JobLease> leases, CancellationToken cancellationToken)
            => _inner.CompleteAsync(leases, cancellationToken);

        public ValueTask<bool> RetryAsync(JobLease lease, TimeSpan delay, string error, CancellationToken cancellationToken)
            => _inner.RetryAsync(lease, delay, error, cancellationToken);

        public ValueTask<bool> DeadLetterAsync(JobLease lease, string error, CancellationToken cancellationToken)
            => _inner.DeadLetterAsync(lease, error, cancellationToken);

        public ValueTask<bool> ReleaseAsync(JobLease lease, TimeSpan delay, CancellationToken cancellationToken)
            => _inner.ReleaseAsync(lease, delay, cancellationToken);

        public ValueTask<int> RequeueAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
            => _inner.RequeueAsync(ids, cancellationToken);

        public ValueTask<int> ExtendLeasesAsync(IReadOnlyList<JobLease> leases, TimeSpan lease, CancellationToken cancellationToken)
            => _inner.ExtendLeasesAsync(leases, lease, cancellationToken);

        public ValueTask<JobSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken)
            => _inner.GetAsync(id, cancellationToken);

        public ValueTask<Guid> EnqueueOnceAsync(NewJob job, CancellationToken cancellationToken)
            => _inner.EnqueueOnceAsync(job, cancellationToken);

        public ValueTask<bool> TryAcquireLeadershipAsync(string name, string owner, TimeSpan lease, CancellationToken cancellationToken)
            => _inner.TryAcquireLeadershipAsync(name, owner, lease, cancellationToken);

        public ValueTask ReleaseLeadershipAsync(string name, string owner, CancellationToken cancellationToken)
            => _inner.ReleaseLeadershipAsync(name, owner, cancellationToken);
    }
}
