using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs;

// What scheduling adds to the in-memory store: the keys that keep a job from being stored twice, and
// the leader lock. Every member runs under the store's one lock.
public sealed partial class InMemoryJobStore
{
    // The pending job that holds each kind's unique key.
    private readonly Dictionary<(string Kind, string Key), Guid> _uniqueKeys = new Dictionary<(string Kind, string Key), Guid>();

    // The job stored for each recurring run. A run is only ever stored ahead of its time, so a run whose
    // time has passed is forgotten.
    private readonly Dictionary<(string TaskKey, DateTimeOffset RunAt), Guid> _runs = new Dictionary<(string TaskKey, DateTimeOffset RunAt), Guid>();
    private readonly Dictionary<string, (string Owner, DateTimeOffset Until)> _leaders = new Dictionary<string, (string Owner, DateTimeOffset Until)>(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<Guid> EnqueueOnceAsync(NewJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            ForgetPastRuns(now);
            (string, DateTimeOffset)? run = job.TaskKey is null ? null : (job.TaskKey, job.RunAt ?? now);
            if (run is { } taken && _runs.TryGetValue(taken, out Guid stored))
            {
                return new ValueTask<Guid>(stored);
            }
            if (job.UniqueKey is null || !_uniqueKeys.TryGetValue((job.Kind, job.UniqueKey), out stored))
            {
                if (_jobs.ContainsKey(job.Id))
                {
                    throw new InvalidOperationException("A job with id " + job.Id + " is already stored.");
                }
                Add(job, now);
                stored = job.Id;
            }
            if (run is { } fresh)
            {
                _runs.Add(fresh, stored);
            }
            return new ValueTask<Guid>(stored);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> TryAcquireLeadershipAsync(string name, string owner, TimeSpan lease, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(name, nameof(name));
        JobLimits.CheckKey(owner, nameof(owner));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_leaders.TryGetValue(name, out (string Owner, DateTimeOffset Until) held) && held.Owner != owner && held.Until > now)
            {
                return new ValueTask<bool>(false);
            }
            _leaders[name] = (owner, now + lease);
            return new ValueTask<bool>(true);
        }
    }

    /// <inheritdoc />
    public ValueTask ReleaseLeadershipAsync(string name, string owner, CancellationToken cancellationToken)
    {
        JobLimits.CheckKey(name, nameof(name));
        JobLimits.CheckKey(owner, nameof(owner));
        lock (_gate)
        {
            if (_leaders.TryGetValue(name, out (string Owner, DateTimeOffset Until) held) && held.Owner == owner)
            {
                _leaders.Remove(name);
            }
        }
        return default;
    }

    private void ForgetPastRuns(DateTimeOffset now)
    {
        List<(string, DateTimeOffset)>? past = null;
        foreach ((string, DateTimeOffset) run in _runs.Keys)
        {
            if (run.Item2 < now)
            {
                (past ??= new List<(string, DateTimeOffset)>()).Add(run);
            }
        }
        if (past is not null)
        {
            foreach ((string, DateTimeOffset) run in past)
            {
                _runs.Remove(run);
            }
        }
    }

    // A job that has finished no longer holds its unique key.
    private void ReleaseUniqueKey(Entry entry)
    {
        if (entry.UniqueKey is not null)
        {
            _uniqueKeys.Remove((entry.Kind, entry.UniqueKey));
        }
    }
}
