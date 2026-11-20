using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// Stores each recurring job's next occurrence as a delayed job, while this host is the leader. Every
/// host that runs a worker runs one; they share a leader lock in the store, so one host at a time
/// stores occurrences, and each occurrence is stored under its task key and time, so a run stored by
/// two hosts at a leader change is still stored once. Occurrences that fall while no host leads are
/// skipped, not caught up.
/// </summary>
internal sealed class RecurringJobService : BackgroundService
{
    /// <summary>The leader lock the hosts share.</summary>
    internal const string LeaderLock = "rogue.recurring";

    private static readonly Action<ILogger, string?, Exception?> _logTickFailed =
        LoggerMessage.Define<string?>(LogLevel.Warning, new EventId(20, "RecurringJobsFailed"),
            "Storing the next occurrence of recurring jobs failed (kind: {JobKind}, or every kind when empty); the scheduler tries again shortly.");

    private static readonly Action<ILogger, Exception?> _logResignFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(21, "RecurringLeaderReleaseFailed"),
            "Giving up the recurring jobs' leader lock failed; another host takes over once its lease runs out.");

    private readonly JobRegistry _registry;
    private readonly JobScheduler _scheduler;
    private readonly IJobStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _owner = Guid.NewGuid().ToString("N");

    public RecurringJobService(JobRegistry registry, JobScheduler scheduler, TimeProvider time, ILogger logger)
    {
        _registry = registry;
        _scheduler = scheduler;
        _store = scheduler.Store;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<RecurringBinding> jobs = _registry.Recurring;
        if (jobs.Count == 0)
        {
            return;
        }

        // The occurrence last stored for each job while this host led, so each is stored once from here.
        var stored = new DateTimeOffset?[jobs.Count];
        try
        {
            while (true)
            {
                TimeSpan wait = await TickAsync(jobs, stored, stoppingToken).ConfigureAwait(false);
                await Task.Delay(wait, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            await ResignAsync().ConfigureAwait(false);
        }
    }

    // Takes or renews the leader lock and, when held, stores each job's next occurrence. Returns how
    // long to wait: until the earliest next occurrence, and never longer than a third of the lease.
    private async Task<TimeSpan> TickAsync(IReadOnlyList<RecurringBinding> jobs, DateTimeOffset?[] stored, CancellationToken stoppingToken)
    {
        TimeSpan lease = _registry.Options.LeaderLeaseDuration;
        TimeSpan wait = lease / 3;
        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (!await _store.TryAcquireLeadershipAsync(LeaderLock, _owner, lease, stoppingToken).ConfigureAwait(false))
            {
                Array.Clear(stored);
                return wait;
            }
            for (int i = 0; i < jobs.Count; i++)
            {
                if (jobs[i].Schedule.NextAfter(now) is not { } next)
                {
                    continue;
                }
                if (stored[i] != next && await TryStoreAsync(jobs[i], next, stoppingToken).ConfigureAwait(false))
                {
                    stored[i] = next;
                }
                if (next - now < wait)
                {
                    wait = next - now;
                }
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logTickFailed(_logger, null, ex);
        }
        return wait;
    }

    // One job failing to store (say, its command outgrew the payload limit) does not hold up the others.
    private async Task<bool> TryStoreAsync(RecurringBinding job, DateTimeOffset at, CancellationToken stoppingToken)
    {
        try
        {
            await _scheduler.StoreAsync(job.Binding, job.Create(at), stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logTickFailed(_logger, job.Binding.Kind, ex);
            return false;
        }
    }

    private async Task ResignAsync()
    {
        try
        {
            await _store.ReleaseLeadershipAsync(LeaderLock, _owner, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logResignFailed(_logger, ex);
        }
    }
}
