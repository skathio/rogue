using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SkathIO.Rogue.Jobs.SqlServer;

/// <summary>
/// SQL Server has no notification a worker can listen on, so this polls instead, adaptively: it probes
/// for jobs that became due since its last probe, from any process, and wakes this process's worker
/// when it finds one. After a find it probes again at <see cref="SqlServerJobsOptions.MinPollInterval"/>;
/// each empty probe doubles the wait, up to <see cref="SqlServerJobsOptions.MaxPollInterval"/>. The
/// probe is one indexed EXISTS, much cheaper than the claim a worker would poll with, and the worker's
/// own poll interval still finds every job when a probe fails.
/// </summary>
internal sealed class SqlServerJobPoller : BackgroundService
{
    private static readonly Action<ILogger, Exception?> _logProbeFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(111, "JobProbeFailed"),
            "Probing for due jobs failed; workers fall back to their own poll interval until a probe succeeds.");

    private readonly SqlServerJobStore _store;
    private readonly Action _wake;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public SqlServerJobPoller(SqlServerJobStore store, Action wake, TimeProvider time, ILogger logger)
    {
        _store = store;
        _wake = wake;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The wait before the next probe: <paramref name="min"/> after a probe that found a job, otherwise
    /// double <paramref name="current"/>, kept within <paramref name="min"/> and <paramref name="max"/>.
    /// </summary>
    internal static TimeSpan NextInterval(TimeSpan current, bool found, TimeSpan min, TimeSpan max)
    {
        if (found || current < min)
        {
            return min;
        }
        return current >= max / 2 ? max : current * 2;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SqlServerJobsOptions options = _store.Options;
        TimeSpan interval = options.MinPollInterval;
        DateTime since = _store.Now();
        try
        {
            while (true)
            {
                await Task.Delay(interval, _time, stoppingToken).ConfigureAwait(false);
                bool found = false;
                DateTime now = _store.Now();
                try
                {
                    found = await _store.HasNewlyDueAsync(since, now, stoppingToken).ConfigureAwait(false);
                    since = now;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logProbeFailed(_logger, ex);
                }
                if (found)
                {
                    _wake();
                }
                interval = NextInterval(interval, found, options.MinPollInterval, options.MaxPollInterval);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
