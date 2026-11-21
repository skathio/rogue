using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Jobs;

namespace SkathIO.Rogue.Consumer.Jobs.Tests;

/// <summary>
/// Passes every call to the app's job store, and reports the first recurring occurrence stored, so a
/// test moves the clock only once the hosted recurring scheduler has stored it, never before.
/// </summary>
internal sealed class WatchedJobStore(IJobStore inner) : IJobStore
{
    private readonly TaskCompletionSource<StoredOccurrence> _firstOccurrence = new TaskCompletionSource<StoredOccurrence>(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the first job with a task key, a recurring job's occurrence, is stored.</summary>
    public Task<StoredOccurrence> FirstOccurrence => _firstOccurrence.Task;

    public async ValueTask<Guid> EnqueueOnceAsync(NewJob job, CancellationToken cancellationToken)
    {
        Guid id = await inner.EnqueueOnceAsync(job, cancellationToken).ConfigureAwait(false);
        if (job.TaskKey is not null)
        {
            _firstOccurrence.TrySetResult(new StoredOccurrence(id, job.Kind, job.RunAt));
        }
        return id;
    }

    public ValueTask EnqueueAsync(IReadOnlyList<NewJob> jobs, CancellationToken cancellationToken) => inner.EnqueueAsync(jobs, cancellationToken);

    public ValueTask<bool> TryAcquireLeadershipAsync(string name, string owner, TimeSpan lease, CancellationToken cancellationToken) =>
        inner.TryAcquireLeadershipAsync(name, owner, lease, cancellationToken);

    public ValueTask ReleaseLeadershipAsync(string name, string owner, CancellationToken cancellationToken) => inner.ReleaseLeadershipAsync(name, owner, cancellationToken);

    public ValueTask<IReadOnlyList<ClaimedJob>> ClaimAsync(string queue, int maxCount, TimeSpan lease, CancellationToken cancellationToken) =>
        inner.ClaimAsync(queue, maxCount, lease, cancellationToken);

    public ValueTask<int> CompleteAsync(IReadOnlyList<JobLease> leases, CancellationToken cancellationToken) => inner.CompleteAsync(leases, cancellationToken);

    public ValueTask<bool> RetryAsync(JobLease lease, TimeSpan delay, string error, CancellationToken cancellationToken) => inner.RetryAsync(lease, delay, error, cancellationToken);

    public ValueTask<bool> DeadLetterAsync(JobLease lease, string error, CancellationToken cancellationToken) => inner.DeadLetterAsync(lease, error, cancellationToken);

    public ValueTask<bool> ReleaseAsync(JobLease lease, TimeSpan delay, CancellationToken cancellationToken) => inner.ReleaseAsync(lease, delay, cancellationToken);

    public ValueTask<int> RequeueAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken) => inner.RequeueAsync(ids, cancellationToken);

    public ValueTask<int> ExtendLeasesAsync(IReadOnlyList<JobLease> leases, TimeSpan lease, CancellationToken cancellationToken) => inner.ExtendLeasesAsync(leases, lease, cancellationToken);

    public ValueTask<JobSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken) => inner.GetAsync(id, cancellationToken);
}

/// <summary>A recurring occurrence as it was stored: the id that holds its task key, its kind and its run time.</summary>
internal sealed record StoredOccurrence(Guid Id, string Kind, DateTimeOffset? RunAt);
