using System;
using System.Text.RegularExpressions;

namespace SkathIO.Rogue.Jobs.Postgres;

/// <summary>Options for the Postgres job store, set through <c>AddRoguePostgresJobs</c>.</summary>
public sealed partial class PostgresJobsOptions
{
    /// <summary>The longest schema name, in characters, so derived names stay within Postgres' 63.</summary>
    public const int MaxSchemaLength = 50;

    /// <summary>
    /// The schema that holds the job tables, and the name of the LISTEN/NOTIFY channel: lower-case
    /// letters, digits and underscores, starting with a letter or underscore, up to
    /// <see cref="MaxSchemaLength"/> characters.
    /// </summary>
    public string Schema { get; set; } = "rogue_jobs";

    /// <summary>
    /// Whether the store creates or upgrades its schema before its first command (the default). When
    /// off, call <see cref="PostgresJobStore.MigrateAsync"/> from a deployment step instead.
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>How long a succeeded job is kept before the pruner deletes it.</summary>
    public TimeSpan SucceededRetention { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How long a dead-lettered job is kept before the pruner deletes it.</summary>
    public TimeSpan DeadLetteredRetention { get; set; } = TimeSpan.FromDays(14);

    /// <summary>How often the pruner runs.</summary>
    public TimeSpan PruneInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The most finished jobs one prune statement deletes, so a large backlog never holds long locks.</summary>
    public int PruneBatchSize { get; set; } = 5_000;

    /// <summary>How long the LISTEN connection waits before it reconnects after an error; polling covers the gap.</summary>
    public TimeSpan ListenReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Checks every option and throws on the first that is out of range.</summary>
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Schema, nameof(Schema));
        if (!SchemaName().IsMatch(Schema))
        {
            throw new ArgumentException("Must be 1 to " + MaxSchemaLength + " lower-case letters, digits or underscores, starting with a letter or underscore.", nameof(Schema));
        }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SucceededRetention, TimeSpan.Zero, nameof(SucceededRetention));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DeadLetteredRetention, TimeSpan.Zero, nameof(DeadLetteredRetention));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PruneInterval, TimeSpan.Zero, nameof(PruneInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(PruneBatchSize, 1, nameof(PruneBatchSize));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ListenReconnectDelay, TimeSpan.Zero, nameof(ListenReconnectDelay));
    }

    internal PostgresJobsOptions Copy() => (PostgresJobsOptions)MemberwiseClone();

    // The schema becomes a quoted identifier in SQL text, which cannot be a parameter, so only names
    // that need no escaping are accepted. \z, not $: $ also matches before a trailing newline.
    [GeneratedRegex(@"^[a-z_][a-z0-9_]{0,49}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaName();
}
