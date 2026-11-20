using System;

namespace SkathIO.Rogue.Jobs.SqlServer;

/// <summary>
/// Options for the SQL Server job store, set through <c>AddRogueSqlServerJobs</c>. The schema also
/// holds the store's table types and sequence.
/// </summary>
public sealed class SqlServerJobsOptions : DatabaseJobsOptions
{
    /// <summary>
    /// How soon the poller looks again after it found a newly due job. SQL Server has no notification
    /// a worker can listen on, so a poller probes for jobs that other processes enqueued and wakes the
    /// idle worker.
    /// </summary>
    public TimeSpan MinPollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The longest the poller waits between probes. Each probe that finds nothing doubles the wait,
    /// from <see cref="MinPollInterval"/> up to this.
    /// </summary>
    public TimeSpan MaxPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    protected override void ValidateStoreOptions()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MinPollInterval, TimeSpan.Zero, nameof(MinPollInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPollInterval, MinPollInterval, nameof(MaxPollInterval));
    }

    internal SqlServerJobsOptions Copy() => (SqlServerJobsOptions)MemberwiseClone();
}
