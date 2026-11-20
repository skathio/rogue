using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SkathIO.Rogue.Jobs;

/// <summary>Options for the job scheduler and worker, set through <c>AddRogueJobs</c>.</summary>
public sealed class JobsOptions
{
    /// <summary>The longest batch one claim may take.</summary>
    public const int MaxBatchSize = 10_000;

    /// <summary>
    /// The source-generated context that serializes job payloads. Required once any job exists; every
    /// job command must be covered by a <c>[JsonSerializable]</c> attribute on it.
    /// </summary>
    public JsonSerializerContext? SerializerContext { get; set; }

    /// <summary>The queue of a job whose command does not name one.</summary>
    public string DefaultQueue { get; set; } = "default";

    /// <summary>
    /// The queues this worker processes. Empty (the default) means every queue a known job uses, plus
    /// <see cref="DefaultQueue"/>.
    /// </summary>
    public IList<string> Queues { get; } = new List<string>();

    /// <summary>How many times a job runs before it is dead-lettered, when its command does not say.</summary>
    public int DefaultMaxAttempts { get; set; } = 5;

    /// <summary>The delay before the first retry, when the command does not say; it doubles on each later retry.</summary>
    public TimeSpan DefaultBackoff { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The longest delay between two attempts.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The fraction, from 0 to 1, of each retry delay that is taken off at random, so jobs that failed
    /// together do not retry together.
    /// </summary>
    public double BackoffJitter { get; set; } = 0.2;

    /// <summary>The most jobs claimed from a queue in one round trip, from 1 to <see cref="MaxBatchSize"/>.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>The most jobs one worker runs at the same time.</summary>
    public int MaxConcurrency { get; set; } = Environment.ProcessorCount;

    /// <summary>How long an idle worker waits before it looks for jobs again, unless an enqueue wakes it.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a claimed job stays reserved for its worker. The worker renews the lease while the job
    /// runs; a job whose worker stopped renewing is claimed again once the lease runs out.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The largest serialized payload the scheduler accepts, in bytes.</summary>
    public int MaxPayloadBytes { get; set; } = 1024 * 1024;

    /// <summary>Checks every option and throws on the first that is out of range.</summary>
    internal void Validate()
    {
        JobLimits.CheckQueue(DefaultQueue, nameof(DefaultQueue));
        foreach (string queue in Queues)
        {
            JobLimits.CheckQueue(queue, nameof(Queues));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(DefaultMaxAttempts, 1, nameof(DefaultMaxAttempts));
        ArgumentOutOfRangeException.ThrowIfLessThan(DefaultBackoff, TimeSpan.Zero, nameof(DefaultBackoff));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBackoff, DefaultBackoff, nameof(MaxBackoff));
        if (!(BackoffJitter >= 0 && BackoffJitter <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(BackoffJitter), BackoffJitter, "Must be from 0 to 1.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1, nameof(BatchSize));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BatchSize, MaxBatchSize, nameof(BatchSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrency, 1, nameof(MaxConcurrency));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero, nameof(PollInterval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LeaseDuration, TimeSpan.Zero, nameof(LeaseDuration));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPayloadBytes, 1, nameof(MaxPayloadBytes));
    }
}
