using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// A job store that keeps jobs in process memory. Jobs are lost when the process stops, so it suits
/// development, tests and work that may be dropped; it is the default store until a durable one is
/// registered. Finished jobs are kept for inspection up to a limit, oldest dropped first.
/// </summary>
public sealed partial class InMemoryJobStore : IJobStore
{
    /// <summary>How many succeeded and how many dead-lettered jobs are kept by default.</summary>
    public const int DefaultFinishedRetention = 10_000;

    private readonly object _gate = new object();
    private readonly TimeProvider _time;
    private readonly int _finishedRetention;
    private readonly Dictionary<Guid, Entry> _jobs = new Dictionary<Guid, Entry>();
    private readonly Dictionary<string, QueueIndex> _queues = new Dictionary<string, QueueIndex>(StringComparer.Ordinal);
    private readonly Queue<(Guid Id, long Sequence)> _succeeded = new Queue<(Guid Id, long Sequence)>();
    private readonly Queue<(Guid Id, long Sequence)> _deadLettered = new Queue<(Guid Id, long Sequence)>();
    private long _sequence;

    /// <summary>Creates a store on the system clock.</summary>
    public InMemoryJobStore()
        : this(TimeProvider.System, DefaultFinishedRetention)
    {
    }

    /// <summary>Creates a store on <paramref name="timeProvider"/>, keeping up to <paramref name="finishedRetention"/> succeeded and dead-lettered jobs each.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="finishedRetention"/> is negative.</exception>
    public InMemoryJobStore(TimeProvider timeProvider, int finishedRetention = DefaultFinishedRetention)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegative(finishedRetention);
        _time = timeProvider;
        _finishedRetention = finishedRetention;
    }

    /// <inheritdoc />
    public ValueTask EnqueueAsync(IReadOnlyList<NewJob> jobs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var batch = new HashSet<Guid>();
            foreach (NewJob job in jobs)
            {
                ArgumentNullException.ThrowIfNull(job, nameof(jobs));
                if (job.IsKeyed)
                {
                    throw new ArgumentException("A job with a unique key or a task key is stored with EnqueueOnceAsync, not in a batch.", nameof(jobs));
                }
                if (_jobs.ContainsKey(job.Id) || !batch.Add(job.Id))
                {
                    throw new InvalidOperationException("A job with id " + job.Id + " is already stored.");
                }
            }

            DateTimeOffset now = _time.GetUtcNow();
            foreach (NewJob job in jobs)
            {
                Add(job, now);
            }
        }
        return default;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ClaimedJob>> ClaimAsync(string queue, int maxCount, TimeSpan lease, CancellationToken cancellationToken)
    {
        JobLimits.CheckQueue(queue, nameof(queue));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_queues.TryGetValue(queue, out QueueIndex? index))
            {
                return new ValueTask<IReadOnlyList<ClaimedJob>>(Array.Empty<ClaimedJob>());
            }

            DateTimeOffset now = _time.GetUtcNow();
            RescueExpired(index, now);

            var claimed = new List<ClaimedJob>(Math.Min(maxCount, index.Ready.Count));
            while (claimed.Count < maxCount && index.Ready.Count > 0)
            {
                ReadyKey next = index.Ready.Min;
                if (next.RunAt > now)
                {
                    break;
                }
                index.Ready.Remove(next);
                Entry entry = _jobs[next.Id];
                entry.State = JobState.Processing;
                entry.Attempt++;
                entry.LeaseUntil = now + lease;
                index.Processing.Add(entry.Id);
                claimed.Add(entry.ToClaimed());
            }
            return new ValueTask<IReadOnlyList<ClaimedJob>>(claimed);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> CompleteAsync(IReadOnlyList<JobLease> leases, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leases);
        return new ValueTask<int>(CountApplied(leases, (lease, now) =>
        {
            if (!TryTakeHeld(lease, now, out Entry? entry))
            {
                return false;
            }
            entry.State = JobState.Succeeded;
            ReleaseUniqueKey(entry);
            Retain(_succeeded, entry);
            return true;
        }));
    }

    /// <inheritdoc />
    public ValueTask<bool> RetryAsync(JobLease lease, TimeSpan delay, string error, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(error);
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (!TryTakeHeld(lease, now, out Entry? entry))
            {
                return new ValueTask<bool>(false);
            }
            entry.LastError = JobLimits.TrimError(error);
            MakeReady(entry, now + delay);
            return new ValueTask<bool>(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> DeadLetterAsync(JobLease lease, string error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_gate)
        {
            if (!TryTakeHeld(lease, _time.GetUtcNow(), out Entry? entry))
            {
                return new ValueTask<bool>(false);
            }
            DeadLetter(entry, error);
            return new ValueTask<bool>(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> ReleaseAsync(JobLease lease, TimeSpan delay, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (!TryTakeHeld(lease, now, out Entry? entry))
            {
                return new ValueTask<bool>(false);
            }
            entry.Attempt--;
            MakeReady(entry, now + delay);
            return new ValueTask<bool>(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> RequeueAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(CountApplied(ids, (id, now) =>
        {
            if (!_jobs.TryGetValue(id, out Entry? entry) || entry.State != JobState.DeadLettered)
            {
                return false;
            }
            if (entry.UniqueKey is not null && !_uniqueKeys.TryAdd((entry.Kind, entry.UniqueKey), entry.Id))
            {
                return false;
            }
            entry.Attempt = 0;
            MakeReady(entry, now);
            return true;
        }));
    }

    /// <inheritdoc />
    public ValueTask<int> ExtendLeasesAsync(IReadOnlyList<JobLease> leases, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        int extended = 0;
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            foreach (JobLease held in leases)
            {
                if (Holds(held, now, out Entry? entry))
                {
                    entry.LeaseUntil = now + lease;
                    extended++;
                }
            }
        }
        return new ValueTask<int>(extended);
    }

    /// <inheritdoc />
    public ValueTask<JobSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return new ValueTask<JobSnapshot?>(_jobs.TryGetValue(id, out Entry? entry) ? entry.ToSnapshot() : null);
        }
    }

    // Applies apply to each item under the lock, at one reading of the clock, and counts the true results.
    private int CountApplied<T>(IReadOnlyList<T> items, Func<T, DateTimeOffset, bool> apply)
    {
        int applied = 0;
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            foreach (T item in items)
            {
                if (apply(item, now))
                {
                    applied++;
                }
            }
        }
        return applied;
    }

    private void Add(NewJob job, DateTimeOffset now)
    {
        var entry = new Entry(job, job.RunAt ?? now, ++_sequence);
        _jobs.Add(job.Id, entry);
        Index(job.Queue).Ready.Add(entry.ReadyKey);
        if (entry.UniqueKey is not null)
        {
            _uniqueKeys.Add((entry.Kind, entry.UniqueKey), entry.Id);
        }
    }


    private QueueIndex Index(string queue)
    {
        if (!_queues.TryGetValue(queue, out QueueIndex? index))
        {
            index = new QueueIndex();
            _queues.Add(queue, index);
        }
        return index;
    }

    // A processing job whose lease ran out is due again now, or dead-lettered when that claim was its last attempt.
    private void RescueExpired(QueueIndex index, DateTimeOffset now)
    {
        List<Entry>? expired = null;
        foreach (Guid id in index.Processing)
        {
            Entry entry = _jobs[id];
            if (entry.LeaseUntil <= now)
            {
                (expired ??= new List<Entry>()).Add(entry);
            }
        }
        if (expired is null)
        {
            return;
        }

        foreach (Entry entry in expired)
        {
            index.Processing.Remove(entry.Id);
            if (entry.Attempt >= entry.MaxAttempts)
            {
                DeadLetter(entry, "The job's lease ran out on its last attempt; its worker stopped without finishing it.");
            }
            else
            {
                MakeReady(entry, now);
            }
        }
    }

    // True when the lease's claim still holds the job: the job is processing, under that attempt, and the lease has not run out.
    private bool Holds(JobLease lease, DateTimeOffset now, out Entry entry)
    {
        if (_jobs.TryGetValue(lease.Id, out Entry? found)
            && found.State == JobState.Processing
            && found.Attempt == lease.Attempt
            && found.LeaseUntil > now)
        {
            entry = found;
            return true;
        }
        entry = null!;
        return false;
    }

    private bool TryTakeHeld(JobLease lease, DateTimeOffset now, out Entry entry)
    {
        if (!Holds(lease, now, out entry))
        {
            return false;
        }
        _queues[entry.Queue].Processing.Remove(entry.Id);
        return true;
    }

    private void MakeReady(Entry entry, DateTimeOffset runAt)
    {
        entry.State = JobState.Enqueued;
        entry.RunAt = runAt;
        entry.Sequence = ++_sequence;
        _queues[entry.Queue].Ready.Add(entry.ReadyKey);
    }

    private void DeadLetter(Entry entry, string error)
    {
        entry.State = JobState.DeadLettered;
        ReleaseUniqueKey(entry);
        entry.LastError = JobLimits.TrimError(error);
        Retain(_deadLettered, entry);
    }

    // Drops the oldest finished jobs past the limit. A job requeued since it finished has a newer
    // sequence than the one recorded here, so it stays.
    private void Retain(Queue<(Guid Id, long Sequence)> finished, Entry entry)
    {
        finished.Enqueue((entry.Id, entry.Sequence));
        while (finished.Count > _finishedRetention)
        {
            (Guid id, long sequence) = finished.Dequeue();
            if (_jobs.TryGetValue(id, out Entry? dropped) && dropped.Sequence == sequence)
            {
                _jobs.Remove(id);
            }
        }
    }
}
