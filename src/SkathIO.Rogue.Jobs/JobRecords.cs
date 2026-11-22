using System;

namespace SkathIO.Rogue.Jobs;

/// <summary>The states a stored job moves through.</summary>
public enum JobState
{
    /// <summary>Waiting for its run time and a worker.</summary>
    Enqueued = 0,

    /// <summary>Claimed by a worker that holds its lease.</summary>
    Processing = 1,

    /// <summary>Its handler completed.</summary>
    Succeeded = 2,

    /// <summary>It failed on its last attempt, or cannot run at all; it is never claimed again.</summary>
    DeadLettered = 3,
}

/// <summary>A job a worker claimed, with the attempt that claim counts. Stores create these.</summary>
public sealed class ClaimedJob
{
    /// <summary>The job id.</summary>
    public required Guid Id { get; init; }

    /// <summary>The stable job kind.</summary>
    public required string Kind { get; init; }

    /// <summary>The queue.</summary>
    public required string Queue { get; init; }

    /// <summary>The serialized command.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>The attempt this claim counts, starting at one.</summary>
    public required int Attempt { get; init; }

    /// <summary>How many times the job runs before it is dead-lettered.</summary>
    public required int MaxAttempts { get; init; }

    /// <summary>The lease that identifies this claim in later store calls.</summary>
    public JobLease Lease => new JobLease(Id, Attempt);
}

/// <summary>Identifies one claim of a job: the job id and the attempt the claim counted.</summary>
/// <param name="Id">The job id.</param>
/// <param name="Attempt">The attempt the claim counted.</param>
public readonly record struct JobLease(Guid Id, int Attempt);

/// <summary>A stored job's state, without its payload. Stores create these.</summary>
public sealed class JobSnapshot
{
    /// <summary>The job id.</summary>
    public required Guid Id { get; init; }

    /// <summary>The stable job kind.</summary>
    public required string Kind { get; init; }

    /// <summary>The queue.</summary>
    public required string Queue { get; init; }

    /// <summary>The current state.</summary>
    public required JobState State { get; init; }

    /// <summary>How many attempts have been counted.</summary>
    public required int Attempt { get; init; }

    /// <summary>How many times the job runs before it is dead-lettered.</summary>
    public required int MaxAttempts { get; init; }

    /// <summary>When the job is next due (for an enqueued job), or was last due.</summary>
    public required DateTimeOffset RunAt { get; init; }

    /// <summary>
    /// The error of the last failed attempt, if any. The worker records the exception's full type
    /// name, never its message, which can quote the payload or a secret; the log has the whole exception.
    /// </summary>
    public string? LastError { get; init; }
}
