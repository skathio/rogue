using System;

namespace SkathIO.Rogue.Jobs.Postgres;

/// <summary>
/// Options for the Postgres job store, set through <c>AddRoguePostgresJobs</c>. The schema also names
/// the LISTEN/NOTIFY channel.
/// </summary>
public sealed class PostgresJobsOptions : DatabaseJobsOptions
{
    /// <summary>How long the LISTEN connection waits before it reconnects after an error; polling covers the gap.</summary>
    public TimeSpan ListenReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    protected override void ValidateStoreOptions()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ListenReconnectDelay, TimeSpan.Zero, nameof(ListenReconnectDelay));
    }

    internal PostgresJobsOptions Copy() => (PostgresJobsOptions)MemberwiseClone();
}
