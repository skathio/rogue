using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SkathIO.Rogue.Jobs.Postgres;

/// <summary>
/// Deletes finished jobs past their retention every <see cref="PostgresJobsOptions.PruneInterval"/>,
/// so the jobs table holds live work plus a bounded history. Every process may run it: each statement
/// deletes a bounded batch, and concurrent deletes of the same rows simply find them gone.
/// </summary>
internal sealed class PostgresJobPruner : BackgroundService
{
    private static readonly Action<ILogger, Exception?> _logPruneFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(102, "JobPruneFailed"),
            "Deleting finished jobs failed; the pruner tries again on its next run.");

    private readonly PostgresJobStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public PostgresJobPruner(PostgresJobStore store, TimeProvider time, ILogger logger)
    {
        _store = store;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_store.Options.PruneInterval, _time, stoppingToken).ConfigureAwait(false);
                try
                {
                    await _store.PruneAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logPruneFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
