using System;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// Marks an <see cref="ICommand"/> or <see cref="ICommand{TResponse}"/> with a handler as a job, and sets
/// its job defaults. Only a marked command can be enqueued or run by the worker, and the
/// serializer-coverage analyzer checks each marked type. A value left at zero or
/// <see langword="null"/> keeps the default in <see cref="JobsOptions"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class JobAttribute : Attribute
{
    /// <summary>Initializes the attribute with every default kept.</summary>
    public JobAttribute()
    {
    }

    /// <summary>
    /// The stable name the job is stored under. Defaults to the command's full type name; set it to keep
    /// queued jobs runnable after the type is renamed or moved.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>The queue the job is enqueued on. Defaults to <see cref="JobsOptions.DefaultQueue"/>.</summary>
    public string? Queue { get; set; }

    /// <summary>How many times the job runs before it is dead-lettered. Defaults to <see cref="JobsOptions.DefaultMaxAttempts"/>.</summary>
    public int MaxAttempts { get; set; }

    /// <summary>The delay before the first retry, in seconds; it doubles on each later retry. Defaults to <see cref="JobsOptions.DefaultBackoff"/>.</summary>
    public int BackoffSeconds { get; set; }

    /// <summary>How long one run may take, in seconds, before it is cancelled and counted as failed. Defaults to no limit.</summary>
    public int TimeoutSeconds { get; set; }
}
