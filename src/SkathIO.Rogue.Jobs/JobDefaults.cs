using System;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// The per-command job defaults read from <see cref="JobAttribute"/> by the source generator. A value
/// left at zero or <see langword="null"/> falls back to <see cref="JobsOptions"/>.
/// </summary>
public sealed class JobDefaults
{
    /// <summary>
    /// The defaults of a command without <see cref="JobAttribute"/>. A definition carrying exactly this
    /// instance is not a job: the worker never runs it and the scheduler refuses it.
    /// </summary>
    public static JobDefaults None { get; } = new JobDefaults(null, 0, 0, 0);

    /// <summary>Creates the defaults; generated code calls this with the attribute's values.</summary>
    /// <exception cref="ArgumentException"><paramref name="queue"/> is empty or too long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count or duration is negative.</exception>
    public JobDefaults(string? queue, int maxAttempts, int backoffSeconds, int timeoutSeconds)
    {
        if (queue is not null)
        {
            JobLimits.CheckQueue(queue, nameof(queue));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(maxAttempts);
        ArgumentOutOfRangeException.ThrowIfNegative(backoffSeconds);
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutSeconds);

        Queue = queue;
        MaxAttempts = maxAttempts;
        BackoffSeconds = backoffSeconds;
        TimeoutSeconds = timeoutSeconds;
    }

    /// <summary>The queue, or <see langword="null"/> for the default queue.</summary>
    public string? Queue { get; }

    /// <summary>The attempt limit, or zero for the default.</summary>
    public int MaxAttempts { get; }

    /// <summary>The first retry delay in seconds, or zero for the default.</summary>
    public int BackoffSeconds { get; }

    /// <summary>The run timeout in seconds, or zero for no limit.</summary>
    public int TimeoutSeconds { get; }
}
