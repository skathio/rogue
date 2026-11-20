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
public sealed class InMemoryJobStore : IJobStore
{
    /// <summary>How many succeeded and how many dead-lettered jobs are kept by default.</summary>
    public const int DefaultFinishedRetention = 10_000;

    private readonly object _gate = new object();
    private readonly TimeProvider _time;
    private readonly int _finishedRetention;
    private readonly Dictionary<Guid, Entry> _jobs = new Dictionary<Guid, Entry>();
    private readonly Dictionary<string, QueueIndex> _queues = new Dictionary<string, QueueIndex>(StringComparer.Ordinal);
    private readonly Queue<Guid> _succeeded = new Queue<Guid>();
    private readonly Queue<Guid> _deadLettered = new Queue<Guid>();
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
                if (_jobs.ContainsKey(job.Id) || !batch.Add(job.Id))
                {
                    throw new InvalidOperationException("A job with id " + job.Id + " is already stored.");
                }
            }

            DateTimeOffset now = _time.GetUtcNow();
            foreach (NewJob job in jobs)
            {
                var entry = new Entry(job, now, ++_sequence);
                _jobs.Add(job.Id, entry);
                Index(job.Queue).Ready.Add(entry.ReadyKey);
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
        int completed = 0;
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            foreach (JobLease lease in leases)
            {
                if (TryTakeHeld(lease, now, out Entry? entry))
                {
                    entry.State = JobState.Succeeded;
                    Retain(_succeeded, entry.Id);
                    completed++;
                }
            }
        }
        return new ValueTask<int>(completed);
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
    public ValueTask<bool> ReleaseAsync(JobLease lease, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (!TryTakeHeld(lease, now, out Entry? entry))
            {
                return new ValueTask<bool>(false);
            }
            entry.Attempt--;
            MakeReady(entry, now);
            return new ValueTask<bool>(true);
        }
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
        entry.LastError = JobLimits.TrimError(error);
        Retain(_deadLettered, entry.Id);
    }

    private void Retain(Queue<Guid> finished, Guid id)
    {
        finished.Enqueue(id);
        while (finished.Count > _finishedRetention)
        {
            _jobs.Remove(finished.Dequeue());
        }
    }

    private sealed class QueueIndex
    {
        public SortedSet<ReadyKey> Ready { get; } = new SortedSet<ReadyKey>();

        public HashSet<Guid> Processing { get; } = new HashSet<Guid>();
    }

    private readonly record struct ReadyKey(DateTimeOffset RunAt, long Sequence, Guid Id) : IComparable<ReadyKey>
    {
        public int CompareTo(ReadyKey other)
        {
            int byTime = RunAt.CompareTo(other.RunAt);
            return byTime != 0 ? byTime : Sequence.CompareTo(other.Sequence);
        }
    }

    private sealed class Entry
    {
        public Entry(NewJob job, DateTimeOffset now, long sequence)
        {
            Id = job.Id;
            Kind = job.Kind;
            Queue = job.Queue;
            Payload = job.Payload.ToArray();
            MaxAttempts = job.MaxAttempts;
            RunAt = now;
            Sequence = sequence;
        }

        public Guid Id { get; }

        public string Kind { get; }

        public string Queue { get; }

        public ReadOnlyMemory<byte> Payload { get; }

        public int MaxAttempts { get; }

        public JobState State { get; set; }

        public int Attempt { get; set; }

        public DateTimeOffset RunAt { get; set; }

        public long Sequence { get; set; }

        public DateTimeOffset LeaseUntil { get; set; }

        public string? LastError { get; set; }

        public ReadyKey ReadyKey => new ReadyKey(RunAt, Sequence, Id);

        public ClaimedJob ToClaimed() => new ClaimedJob
        {
            Id = Id,
            Kind = Kind,
            Queue = Queue,
            Payload = Payload,
            Attempt = Attempt,
            MaxAttempts = MaxAttempts,
        };

        public JobSnapshot ToSnapshot() => new JobSnapshot
        {
            Id = Id,
            Kind = Kind,
            Queue = Queue,
            State = State,
            Attempt = Attempt,
            MaxAttempts = MaxAttempts,
            RunAt = RunAt,
            LastError = LastError,
        };
    }
}
