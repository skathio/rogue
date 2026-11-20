using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// Durable job storage. A job is one record whose state moves from
/// <see cref="JobState.Enqueued"/> to <see cref="JobState.Processing"/> when a worker claims it, and on
/// to <see cref="JobState.Succeeded"/>, back to <see cref="JobState.Enqueued"/> for a retry, or to
/// <see cref="JobState.DeadLettered"/>.
/// </summary>
/// <remarks>
/// <para>
/// A claim reserves a job for a lease and counts an attempt. Every later call for the job carries the
/// <see cref="JobLease"/> from that claim, and applies only while that claim still holds the job: once a
/// lease runs out the job can be claimed again, and the earlier worker's calls return
/// <see langword="false"/> instead of overwriting the newer claim.
/// </para>
/// <para>
/// Time is the store's own clock. Implementations must behave the same on every database, which the
/// shared store contract tests check.
/// </para>
/// </remarks>
public interface IJobStore
{
    /// <summary>
    /// Stores <paramref name="jobs"/>, all or none, ready to be claimed now. A job whose id is already
    /// stored, or appears twice in the batch, fails the whole batch.
    /// </summary>
    ValueTask EnqueueAsync(IReadOnlyList<NewJob> jobs, CancellationToken cancellationToken);

    /// <summary>
    /// Claims up to <paramref name="maxCount"/> jobs of <paramref name="queue"/> that are due, oldest run
    /// time first and in enqueue order on a tie, and reserves each for <paramref name="lease"/>. A job
    /// whose earlier lease ran out is due again; if that claim used its last attempt the job is
    /// dead-lettered instead of returned. No two concurrent claims return the same job.
    /// </summary>
    ValueTask<IReadOnlyList<ClaimedJob>> ClaimAsync(string queue, int maxCount, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Marks the claimed jobs succeeded and returns how many claims still held their job.</summary>
    ValueTask<int> CompleteAsync(IReadOnlyList<JobLease> leases, CancellationToken cancellationToken);

    /// <summary>Puts a claimed job back to run again after <paramref name="delay"/>, recording <paramref name="error"/>.</summary>
    ValueTask<bool> RetryAsync(JobLease lease, TimeSpan delay, string error, CancellationToken cancellationToken);

    /// <summary>Moves a claimed job to the dead letters, recording <paramref name="error"/>; it is never claimed again.</summary>
    ValueTask<bool> DeadLetterAsync(JobLease lease, string error, CancellationToken cancellationToken);

    /// <summary>Puts a claimed job back without counting the attempt, for a worker that is shutting down.</summary>
    ValueTask<bool> ReleaseAsync(JobLease lease, CancellationToken cancellationToken);

    /// <summary>Extends the leases of running jobs to <paramref name="lease"/> from now and returns how many claims still held their job.</summary>
    ValueTask<int> ExtendLeasesAsync(IReadOnlyList<JobLease> leases, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Returns the job's current state, or <see langword="null"/> when no such job is stored.</summary>
    ValueTask<JobSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken);
}
