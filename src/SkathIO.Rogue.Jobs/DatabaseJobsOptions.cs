using System;
using System.Text.RegularExpressions;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// The options every database job store shares: where its tables live, whether it migrates them
/// itself, and how long finished jobs are kept.
/// </summary>
public abstract partial class DatabaseJobsOptions
{
    /// <summary>The longest schema name, in characters, so the names a store derives from it stay within every database's limit.</summary>
    public const int MaxSchemaLength = 50;

    /// <summary>
    /// The schema that holds the job tables: lower-case letters, digits and underscores, starting with
    /// a letter or underscore, up to <see cref="MaxSchemaLength"/> characters.
    /// </summary>
    public string Schema { get; set; } = "rogue_jobs";

    /// <summary>
    /// Whether the store creates or upgrades its schema before its first command (the default). When
    /// off, call the store's <c>MigrateAsync</c> from a deployment step instead.
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

    /// <summary>
    /// Checks every option, the shared ones and then the store's own, and throws on the first that is
    /// out of range. Stores call it when they are created and registered.
    /// </summary>
    /// <exception cref="ArgumentNullException"><see cref="Schema"/> is null.</exception>
    /// <exception cref="ArgumentException"><see cref="Schema"/> is not a plain name.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A duration, the batch size or a store's own option is out of range.</exception>
    public void Validate()
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
        ValidateStoreOptions();
    }

    /// <summary>Checks the store's own options, after the shared ones; throws on the first that is out of range.</summary>
    protected virtual void ValidateStoreOptions()
    {
    }

    // The schema is spliced into SQL text as a quoted identifier, which cannot be a parameter, so only
    // names that need no escaping are accepted. \z, not $: $ also matches before a trailing newline.
    [GeneratedRegex(@"^[a-z_][a-z0-9_]{0,49}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaName();
}
