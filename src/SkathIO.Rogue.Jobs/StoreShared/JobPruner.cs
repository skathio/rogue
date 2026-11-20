using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SkathIO.Rogue.Jobs.Stores;

/// <summary>
/// Deletes finished jobs past their retention every prune interval, so a database store's jobs table
/// holds live work plus a bounded history. Every process may run it: each statement deletes a bounded
/// batch, and concurrent deletes of the same rows simply find them gone.
/// </summary>
/// <remarks>Shared source: each database store package compiles its own internal copy.</remarks>
internal sealed class JobPruner : BackgroundService
{
    private static readonly Action<ILogger, Exception?> _logPruneFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(102, "JobPruneFailed"),
            "Deleting finished jobs failed; the pruner tries again on its next run.");

    private readonly Func<CancellationToken, Task<int>> _prune;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <param name="prune">Deletes the finished jobs past their retention; the store's PruneAsync.</param>
    /// <param name="interval">How long to wait before each run.</param>
    /// <param name="time">The clock the waits run on.</param>
    /// <param name="logger">Where a failed run is logged.</param>
    public JobPruner(Func<CancellationToken, Task<int>> prune, TimeSpan interval, TimeProvider time, ILogger logger)
    {
        _prune = prune;
        _interval = interval;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_interval, _time, stoppingToken).ConfigureAwait(false);
                try
                {
                    await _prune(stoppingToken).ConfigureAwait(false);
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
