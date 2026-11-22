using System;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// A job to store: its id, kind, queue, serialized command and attempt limit, and optionally the time
/// it is due and the keys that keep it from being stored twice. Each part is checked against the store
/// contract's limits as it is set.
/// </summary>
public sealed class NewJob
{
    private readonly Guid _id;
    private readonly string _kind = string.Empty;
    private readonly string _queue = string.Empty;
    private readonly int _maxAttempts;
    private readonly string? _uniqueKey;
    private readonly string? _taskKey;

    /// <summary>The job id; never empty.</summary>
    /// <exception cref="ArgumentException">The id is empty.</exception>
    public required Guid Id
    {
        get => _id;
        init => _id = value != Guid.Empty ? value : throw new ArgumentException("A job id must not be empty.", nameof(Id));
    }

    /// <summary>The stable job kind, up to <see cref="JobLimits.MaxKindLength"/> characters with no white space.</summary>
    /// <exception cref="ArgumentException">The kind is empty, too long or has white space.</exception>
    public required string Kind
    {
        get => _kind;
        init
        {
            JobLimits.CheckKind(value, nameof(Kind));
            _kind = value;
        }
    }

    /// <summary>The queue, up to <see cref="JobLimits.MaxQueueLength"/> characters with no white space.</summary>
    /// <exception cref="ArgumentException">The queue is empty, too long or has white space.</exception>
    public required string Queue
    {
        get => _queue;
        init
        {
            JobLimits.CheckQueue(value, nameof(Queue));
            _queue = value;
        }
    }

    /// <summary>The serialized command.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>How many times the job runs before it is dead-lettered; at least one.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than one.</exception>
    public required int MaxAttempts
    {
        get => _maxAttempts;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(MaxAttempts));
            _maxAttempts = value;
        }
    }

    /// <summary>When the job is due; <see langword="null"/> (the default) means when it is stored.</summary>
    public DateTimeOffset? RunAt { get; init; }

    /// <summary>
    /// A key that at most one pending (enqueued or processing) job of the same kind holds: storing
    /// another while one is pending stores nothing and returns the pending job's id. Up to
    /// <see cref="JobLimits.MaxKeyLength"/> characters with no white space; <see langword="null"/> for none.
    /// </summary>
    /// <exception cref="ArgumentException">The key is empty, too long or has white space.</exception>
    public string? UniqueKey
    {
        get => _uniqueKey;
        init
        {
            if (value is not null)
            {
                JobLimits.CheckKey(value, nameof(UniqueKey));
            }
            _uniqueKey = value;
        }
    }

    /// <summary>
    /// The recurring task this job is one run of: one job is stored for each task key and
    /// <see cref="RunAt"/>, however often it is stored. Up to <see cref="JobLimits.MaxKeyLength"/>
    /// characters with no white space; <see langword="null"/> for none.
    /// </summary>
    /// <exception cref="ArgumentException">The key is empty, too long or has white space.</exception>
    public string? TaskKey
    {
        get => _taskKey;
        init
        {
            if (value is not null)
            {
                JobLimits.CheckKey(value, nameof(TaskKey));
            }
            _taskKey = value;
        }
    }

    /// <summary>True when the job carries a unique key or a task key, and so is stored only by <see cref="IJobStore.EnqueueOnceAsync"/>.</summary>
    internal bool IsKeyed => _uniqueKey is not null || _taskKey is not null;
}
