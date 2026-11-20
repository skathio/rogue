using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// Claims jobs from the store and runs them, each in a service scope of its own, through the Rogue
/// dispatcher. A failed run is retried after an exponential backoff until its attempts are used up,
/// then dead-lettered; a payload that cannot be read, or a kind this process does not know, is
/// dead-lettered at once.
/// </summary>
/// <remarks>
/// Run it from the host: call <see cref="RunAsync"/> from a hosted service's execute method with the
/// host's stopping token. On shutdown, running jobs are cancelled and put back without counting the
/// attempt.
/// </remarks>
public sealed class JobWorker
{
    private static readonly Action<ILogger, Guid, string, int, int, TimeSpan, Exception?> _logRetry =
        LoggerMessage.Define<Guid, string, int, int, TimeSpan>(LogLevel.Warning, new EventId(1, "JobRetry"),
            "Job {JobId} ({JobKind}) failed on attempt {Attempt} of {MaxAttempts}; it runs again in {Delay}.");

    private static readonly Action<ILogger, Guid, string, int, Exception?> _logDeadLetter =
        LoggerMessage.Define<Guid, string, int>(LogLevel.Error, new EventId(2, "JobDeadLettered"),
            "Job {JobId} ({JobKind}) was dead-lettered after attempt {Attempt}.");

    private static readonly Action<ILogger, Guid, Exception?> _logSettleFailed =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(3, "JobOutcomeNotStored"),
            "The outcome of job {JobId} could not be stored; the job runs again once its lease runs out.");

    private static readonly Action<ILogger, Exception?> _logClaimFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(4, "JobClaimFailed"),
            "Claiming jobs failed; the worker tries again after its poll interval.");

    private static readonly Action<ILogger, Exception?> _logRenewFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(5, "JobLeaseRenewFailed"),
            "Renewing the leases of running jobs failed; the worker tries again shortly.");

    private readonly JobRegistry _registry;
    private readonly IJobStore _store;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly JobSignal _signal;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, JobLease> _running = new ConcurrentDictionary<Guid, JobLease>();
    private int _nextQueue;

    // Built only by AddRogueJobs, which registers every service read here.
    internal JobWorker(JobRegistry registry, IJobStore store, IServiceProvider services)
    {
        _registry = registry;
        _store = store;
        _scopes = services.GetRequiredService<IServiceScopeFactory>();
        _time = services.GetRequiredService<TimeProvider>();
        _signal = services.GetRequiredService<JobSignal>();
        _logger = (ILogger?)services.GetService<ILoggerFactory>()?.CreateLogger("SkathIO.Rogue.Jobs.JobWorker") ?? NullLogger.Instance;
    }

    /// <summary>
    /// Runs jobs until <paramref name="stoppingToken"/> is cancelled, at most
    /// <see cref="JobsOptions.MaxConcurrency"/> at a time, and returns once the running jobs have stopped.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        JobsOptions options = _registry.Options;
        using var slots = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        Task renewing = RenewLeasesUntilStoppedAsync(stoppingToken);
        bool holdingSlot = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await slots.WaitAsync(stoppingToken).ConfigureAwait(false);
                holdingSlot = true;
                IReadOnlyList<ClaimedJob> claimed = await ClaimNextAsync(Math.Min(slots.CurrentCount + 1, options.BatchSize), stoppingToken).ConfigureAwait(false);
                if (claimed.Count == 0)
                {
                    slots.Release();
                    holdingSlot = false;
                    await _signal.WaitAsync(options.PollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // The first job runs in the slot already held. Only this loop takes slots, so the ones
                // counted before the claim are still free for the rest.
                holdingSlot = false;
                for (int i = 0; i < claimed.Count; i++)
                {
                    if (i > 0)
                    {
                        await slots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    _ = RunInSlotAsync(claimed[i], slots, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            if (holdingSlot)
            {
                slots.Release();
            }
            for (int i = 0; i < options.MaxConcurrency; i++)
            {
                await slots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await renewing.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one round: claims up to <see cref="JobsOptions.BatchSize"/> due jobs from each queue once,
    /// runs them, and returns how many ran. For tests and for hosts that drain jobs on a schedule.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        JobsOptions options = _registry.Options;
        using var slots = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        int ran = 0;
        foreach (string queue in _registry.Queues)
        {
            IReadOnlyList<ClaimedJob> claimed = await _store.ClaimAsync(queue, options.BatchSize, options.LeaseDuration, cancellationToken).ConfigureAwait(false);
            var running = new Task[claimed.Count];
            for (int i = 0; i < claimed.Count; i++)
            {
                // Not cancellable: every claimed job must run or be put back, never abandoned.
                await slots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                running[i] = RunInSlotAsync(claimed[i], slots, cancellationToken);
            }
            await Task.WhenAll(running).ConfigureAwait(false);
            ran += claimed.Count;
        }
        return ran;
    }

    /// <summary>Extends the leases of the running jobs and returns how many the store still held.</summary>
    internal async Task<int> RenewLeasesAsync(CancellationToken cancellationToken)
    {
        if (_running.IsEmpty)
        {
            return 0;
        }
        var leases = new List<JobLease>(_running.Values);
        return await _store.ExtendLeasesAsync(leases, _registry.Options.LeaseDuration, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one claimed job and stores its outcome; returns the outcome's telemetry name. Never throws.</summary>
    internal async Task<string> ExecuteAsync(ClaimedJob job, CancellationToken stoppingToken)
    {
        _running[job.Id] = job.Lease;
        long started = _time.GetTimestamp();
        using Activity? activity = RogueJobsTelemetry.Source.StartActivity(RogueJobsTelemetry.ExecuteActivity, ActivityKind.Consumer);
        activity?.SetTag(RogueJobsTelemetry.KindTag, job.Kind);
        activity?.SetTag(RogueJobsTelemetry.QueueTag, job.Queue);
        activity?.SetTag(RogueJobsTelemetry.AttemptTag, job.Attempt);

        Outcome outcome = await RunJobAsync(job, stoppingToken).ConfigureAwait(false);
        await SettleAsync(job, outcome).ConfigureAwait(false);
        _running.TryRemove(job.Id, out _);

        var tags = new TagList
        {
            { RogueJobsTelemetry.KindTag, job.Kind },
            { RogueJobsTelemetry.QueueTag, job.Queue },
            { RogueJobsTelemetry.OutcomeTag, outcome.Name },
        };
        if (outcome.ErrorType is not null)
        {
            tags.Add(RogueJobsTelemetry.ErrorTypeTag, outcome.ErrorType);
            activity?.SetTag(RogueJobsTelemetry.ErrorTypeTag, outcome.ErrorType);
            activity?.SetStatus(ActivityStatusCode.Error);
        }
        activity?.SetTag(RogueJobsTelemetry.OutcomeTag, outcome.Name);
        RogueJobsTelemetry.Executed.Add(1, tags);
        RogueJobsTelemetry.Duration.Record(_time.GetElapsedTime(started).TotalMilliseconds, tags);
        return outcome.Name;
    }

    private async Task RunInSlotAsync(ClaimedJob job, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        try
        {
            await ExecuteAsync(job, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            slots.Release();
        }
    }

    // Tries each queue in turn, starting after the one the last claim came from, so no queue starves.
    private async Task<IReadOnlyList<ClaimedJob>> ClaimNextAsync(int maxCount, CancellationToken stoppingToken)
    {
        IReadOnlyList<string> queues = _registry.Queues;
        try
        {
            for (int tried = 0; tried < queues.Count; tried++)
            {
                int index = _nextQueue;
                _nextQueue = (index + 1) % queues.Count;
                IReadOnlyList<ClaimedJob> claimed = await _store.ClaimAsync(queues[index], maxCount, _registry.Options.LeaseDuration, stoppingToken).ConfigureAwait(false);
                if (claimed.Count > 0)
                {
                    return claimed;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _logClaimFailed(_logger, ex);
        }
        return Array.Empty<ClaimedJob>();
    }

    private async Task RenewLeasesUntilStoppedAsync(CancellationToken stoppingToken)
    {
        TimeSpan period = _registry.Options.LeaseDuration / 3;
        try
        {
            while (true)
            {
                await Task.Delay(period, _time, stoppingToken).ConfigureAwait(false);
                try
                {
                    await RenewLeasesAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logRenewFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task<Outcome> RunJobAsync(ClaimedJob job, CancellationToken stoppingToken)
    {
        if (!_registry.TryGet(job.Kind, out JobBinding? binding))
        {
            return Outcome.DeadLetter(new InvalidOperationException("No job kind '" + job.Kind + "' is registered in this process."));
        }

        try
        {
            await InvokeAsync(binding, job, stoppingToken).ConfigureAwait(false);
            return Outcome.Complete;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return Outcome.Release;
        }
        catch (JobPayloadException ex)
        {
            return Outcome.DeadLetter(ex);
        }
        catch (Exception ex)
        {
            if (job.Attempt >= job.MaxAttempts)
            {
                return Outcome.DeadLetter(ex);
            }
            JobsOptions options = _registry.Options;
            TimeSpan delay = JobBackoff.Jitter(JobBackoff.Delay(binding.Backoff, job.Attempt, options.MaxBackoff), options.BackoffJitter, Random.Shared.NextDouble());
            return Outcome.Retry(delay, ex);
        }
    }

    private async Task InvokeAsync(JobBinding binding, ClaimedJob job, CancellationToken stoppingToken)
    {
        using CancellationTokenSource? timeout = binding.Timeout is { } limit ? new CancellationTokenSource(limit, _time) : null;
        using CancellationTokenSource? linked = timeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        AsyncServiceScope scope = _scopes.CreateAsyncScope();
        try
        {
            await using (scope.ConfigureAwait(false))
            {
                RogueDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<RogueDispatcher>();
                await binding.InvokeAsync(dispatcher, job.Payload, linked?.Token ?? stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (timeout is { IsCancellationRequested: true } && !stoppingToken.IsCancellationRequested)
        {
            throw new TimeoutException("The job did not finish within " + binding.Timeout + ".", ex);
        }
    }

    private async Task SettleAsync(ClaimedJob job, Outcome outcome)
    {
        JobLease lease = job.Lease;
        try
        {
            switch (outcome.Name)
            {
                case RogueJobsTelemetry.Succeeded:
                    await _store.CompleteAsync(new[] { lease }, CancellationToken.None).ConfigureAwait(false);
                    break;
                case RogueJobsTelemetry.Released:
                    await _store.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
                    break;
                case RogueJobsTelemetry.Retried:
                    _logRetry(_logger, job.Id, job.Kind, job.Attempt, job.MaxAttempts, outcome.Delay, outcome.Exception);
                    await _store.RetryAsync(lease, outcome.Delay, outcome.Error!, CancellationToken.None).ConfigureAwait(false);
                    break;
                default:
                    _logDeadLetter(_logger, job.Id, job.Kind, job.Attempt, outcome.Exception);
                    await _store.DeadLetterAsync(lease, outcome.Error!, CancellationToken.None).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logSettleFailed(_logger, job.Id, ex);
        }
    }

    private readonly struct Outcome
    {
        private Outcome(string name, TimeSpan delay, Exception? exception)
        {
            Name = name;
            Delay = delay;
            Exception = exception;
        }

        public static Outcome Complete => new Outcome(RogueJobsTelemetry.Succeeded, TimeSpan.Zero, null);

        public static Outcome Release => new Outcome(RogueJobsTelemetry.Released, TimeSpan.Zero, null);

        public string Name { get; }

        public TimeSpan Delay { get; }

        public Exception? Exception { get; }

        /// <summary>The error recorded with the job: the exception's type and message, cut to the store's limit.</summary>
        public string? Error => Exception is null ? null : JobLimits.TrimError(Exception.GetType().FullName + ": " + Exception.Message);

        /// <summary>The exception's type name, the only part of it telemetry carries.</summary>
        public string? ErrorType => Exception?.GetType().Name;

        public static Outcome Retry(TimeSpan delay, Exception exception) => new Outcome(RogueJobsTelemetry.Retried, delay, exception);

        public static Outcome DeadLetter(Exception exception) => new Outcome(RogueJobsTelemetry.DeadLettered, TimeSpan.Zero, exception);
    }
}
