using System;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs.Stores;

/// <summary>
/// Runs a database store's migrations once, before its first command, when the store migrates itself.
/// A failed or cancelled migration is retried by the next command instead of failing every later one.
/// </summary>
/// <remarks>Shared source: each database store package compiles its own internal copy.</remarks>
internal sealed class MigrationGate
{
    private readonly bool _enabled;
    private readonly Func<CancellationToken, Task> _migrate;
    private readonly object _gate = new object();
    private Task? _migration;

    /// <param name="enabled">Whether the store migrates itself; when false, the gate never waits.</param>
    /// <param name="migrate">The store's MigrateAsync.</param>
    public MigrationGate(bool enabled, Func<CancellationToken, Task> migrate)
    {
        _enabled = enabled;
        _migrate = migrate;
    }

    /// <summary>Completes once the schema is current, starting the migration if none has succeeded yet.</summary>
    public Task ReadyAsync(CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            return Task.CompletedTask;
        }
        Task migration;
        lock (_gate)
        {
            if (_migration is null || _migration.IsFaulted || _migration.IsCanceled)
            {
                _migration = _migrate(CancellationToken.None);
            }
            migration = _migration;
        }
        return migration.IsCompletedSuccessfully ? Task.CompletedTask : migration.WaitAsync(cancellationToken);
    }
}
