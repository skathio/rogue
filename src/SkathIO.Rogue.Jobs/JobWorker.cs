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
/// Claims jobs from the store and runs them through the Rogue dispatcher, each in a service scope of
/// its own, or of its claimed batch when <see cref="JobsOptions.ScopePerBatch"/> is on. A failed run is retried after an exponential backoff until its attempts are used up,
/// then dead-lettered; a payload that cannot be read is dead-lettered at once. A job of a kind this
/// process does not know is put back for later without counting the attempt, so during a rolling
/// deploy an older worker leaves a newer kind to a worker that knows it.
/// </summary>
/// <remarks>
/// <c>AddRogueJobWorker</c> registers a hosted service that calls <see cref="RunAsync"/> with the host's
/// stopping token; without it no worker runs, and a host can still drain jobs itself through
/// <see cref="RunOnceAsync"/>. On shutdown, running jobs are
/// cancelled and put back without counting the attempt. Every claimed job keeps its lease, renewed
/// every third of <see cref="JobsOptions.LeaseDuration"/>, until its outcome is stored.
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

    private static readonly Action<ILogger, int, int, Exception?> _logRenewLost =
        LoggerMessage.Define<int, int>(LogLevel.Warning, new EventId(6, "JobLeaseLost"),
            "The store no longer held {Lost} of the {Held} job leases this worker renewed; those jobs may run again on another worker.");

    private static readonly Action<ILogger, Guid, string, string, Exception?> _logSettleLost =
        LoggerMessage.Define<Guid, string, string>(LogLevel.Warning, new EventId(7, "JobOutcomeLeaseLost"),
            "The store no longer held the lease of job {JobId} ({JobKind}), so its outcome '{Outcome}' was not recorded; the job may run again.");

    private static readonly Action<ILogger, Guid, string, TimeSpan, Exception?> _logUnknownKind =
        LoggerMessage.Define<Guid, string, TimeSpan>(LogLevel.Warning, new EventId(8, "JobKindUnknown"),
            "Job {JobId} has the kind '{JobKind}', which this process does not know; it is put back to run in {Delay}.");

    private static readonly Action<ILogger, Exception?> _logBatchScopeDisposeFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(9, "JobBatchScopeDisposeFailed"),
            "Disposing the service scope a batch of jobs shared failed after every job in it had run.");

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

        // Renewal stops only once every claimed job has settled, not when the host starts stopping:
        // jobs still being put back keep their leases until they are.
        using var renewal = new CancellationTokenSource();
        Task renewing = RenewLeasesUntilAsync(renewal.Token);
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
                Hold(claimed);
                BatchScope? batch = StartBatch(claimed.Count);
                for (int i = 0; i < claimed.Count; i++)
                {
                    if (i > 0)
                    {
                        await slots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    _ = RunInSlotAsync(claimed[i], slots, batch, stoppingToken);
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
            await renewal.CancelAsync().ConfigureAwait(false);
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

        // A batch can outlast one lease while its jobs wait for a slot, so this path renews as well,
        // until every job it claimed has settled.
        using var renewal = new CancellationTokenSource();
        Task renewing = RenewLeasesUntilAsync(renewal.Token);
        int ran = 0;
        try
        {
            foreach (string queue in _registry.Queues)
            {
                IReadOnlyList<ClaimedJob> claimed = await _store.ClaimAsync(queue, options.BatchSize, options.LeaseDuration, cancellationToken).ConfigureAwait(false);
                Hold(claimed);
                BatchScope? batch = StartBatch(claimed.Count);
                var running = new Task[claimed.Count];
                for (int i = 0; i < claimed.Count; i++)
                {
                    // Not cancellable: every claimed job must run or be put back, never abandoned.
                    await slots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    running[i] = RunInSlotAsync(claimed[i], slots, batch, cancellationToken);
                }
                await Task.WhenAll(running).ConfigureAwait(false);
                ran += claimed.Count;
            }
        }
        finally
        {
            await renewal.CancelAsync().ConfigureAwait(false);
            await renewing.ConfigureAwait(false);
        }
        return ran;
    }

    /// <summary>
    /// Extends the leases of the claimed jobs that have not settled, waiting or running, and returns
    /// how many the store still held; logs a warning when it held fewer.
    /// </summary>
    internal async Task<int> RenewLeasesAsync(CancellationToken cancellationToken)
    {
        if (_running.IsEmpty)
        {
            return 0;
        }
        var leases = new List<JobLease>(_running.Values);
        int held = await _store.ExtendLeasesAsync(leases, _registry.Options.LeaseDuration, cancellationToken).ConfigureAwait(false);
        if (held < leases.Count)
        {
            _logRenewLost(_logger, leases.Count - held, leases.Count, null);
        }
        return held;
    }

    /// <summary>
    /// Runs one claimed job, in <paramref name="batch"/>'s scope when given and otherwise in a scope of
    /// its own, and stores its outcome; returns the outcome's telemetry name. Never throws.
    /// </summary>
    internal async Task<string> ExecuteAsync(ClaimedJob job, CancellationToken stoppingToken, BatchScope? batch = null)
    {
        _running[job.Id] = job.Lease;
        long started = _time.GetTimestamp();
        using Activity? activity = RogueJobsTelemetry.Source.StartActivity(RogueJobsTelemetry.ExecuteActivity, ActivityKind.Consumer);
        activity?.SetTag(RogueJobsTelemetry.KindTag, job.Kind);
        activity?.SetTag(RogueJobsTelemetry.QueueTag, job.Queue);
        activity?.SetTag(RogueJobsTelemetry.AttemptTag, job.Attempt);

        Outcome outcome = await RunJobAsync(job, batch, stoppingToken).ConfigureAwait(false);
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

    // Puts claimed jobs under renewal from the moment they are claimed; each leaves once settled.
    private void Hold(IReadOnlyList<ClaimedJob> claimed)
    {
        foreach (ClaimedJob job in claimed)
        {
            _running[job.Id] = job.Lease;
        }
    }

    // One scope for a whole claimed batch when the options ask for it; null means a scope per job.
    private BatchScope? StartBatch(int jobs) =>
        _registry.Options.ScopePerBatch && jobs > 0 ? new BatchScope(_scopes.CreateAsyncScope(), jobs) : null;

    private async Task RunInSlotAsync(ClaimedJob job, SemaphoreSlim slots, BatchScope? batch, CancellationToken stoppingToken)
    {
        try
        {
            await ExecuteAsync(job, stoppingToken, batch).ConfigureAwait(false);
        }
        finally
        {
            await LeaveBatchAsync(batch).ConfigureAwait(false);
            slots.Release();
        }
    }

    // The batch's last job disposes its scope. A scoped service that throws on dispose fails no job,
    // since every job of the batch has already settled, so it is logged instead.
    private async ValueTask LeaveBatchAsync(BatchScope? batch)
    {
        if (batch is null)
        {
            return;
        }
        try
        {
            await batch.LeaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logBatchScopeDisposeFailed(_logger, ex);
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

    // Renews every third of a lease until stopped. A failed renewal, a cancelled store command
    // included, is logged and tried again on the next round; only the stop token ends the loop.
    private async Task RenewLeasesUntilAsync(CancellationToken stop)
    {
        TimeSpan period = _registry.Options.LeaseDuration / 3;
        try
        {
            while (true)
            {
                await Task.Delay(period, _time, stop).ConfigureAwait(false);
                try
                {
                    await RenewLeasesAsync(stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stop.IsCancellationRequested)
                {
                    _logRenewFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Every claimed job has settled.
        }
    }

    private async Task<Outcome> RunJobAsync(ClaimedJob job, BatchScope? batch, CancellationToken stoppingToken)
    {
        JobsOptions options = _registry.Options;
        if (!_registry.TryGet(job.Kind, out JobBinding? binding))
        {
            // Never shorter than the poll interval, so a zero backoff cannot make the worker claim
            // and put back the same job in a tight loop.
            TimeSpan later = JobBackoff.Jitter(options.DefaultBackoff, options.BackoffJitter, Random.Shared.NextDouble());
            later = later > options.PollInterval ? later : options.PollInterval;
            _logUnknownKind(_logger, job.Id, job.Kind, later, null);
            return Outcome.PutBack(later);
        }

        try
        {
            await InvokeAsync(binding, job, batch, stoppingToken).ConfigureAwait(false);
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
            TimeSpan delay = JobBackoff.Jitter(JobBackoff.Delay(binding.Backoff, job.Attempt, options.MaxBackoff), options.BackoffJitter, Random.Shared.NextDouble());
            return Outcome.Retry(delay, ex);
        }
    }

    private async Task InvokeAsync(JobBinding binding, ClaimedJob job, BatchScope? batch, CancellationToken stoppingToken)
    {
        using CancellationTokenSource? timeout = binding.Timeout is { } limit ? new CancellationTokenSource(limit, _time) : null;
        using CancellationTokenSource? linked = timeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        CancellationToken token = linked?.Token ?? stoppingToken;
        try
        {
            if (batch is not null)
            {
                await DispatchAsync(binding, batch.Services, job, token).ConfigureAwait(false);
                return;
            }
            AsyncServiceScope scope = _scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                await DispatchAsync(binding, scope.ServiceProvider, job, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (timeout is { IsCancellationRequested: true } && !stoppingToken.IsCancellationRequested)
        {
            throw new TimeoutException("The job did not finish within " + binding.Timeout + ".", ex);
        }
    }

    private static ValueTask DispatchAsync(JobBinding binding, IServiceProvider services, ClaimedJob job, CancellationToken cancellationToken) =>
        binding.InvokeAsync(services.GetRequiredService<RogueDispatcher>(), job.Payload, cancellationToken);

    private async Task SettleAsync(ClaimedJob job, Outcome outcome)
    {
        JobLease lease = job.Lease;
        try
        {
            bool stored;
            switch (outcome.Name)
            {
                case RogueJobsTelemetry.Succeeded:
                    stored = await _store.CompleteAsync(new[] { lease }, CancellationToken.None).ConfigureAwait(false) > 0;
                    break;
                case RogueJobsTelemetry.Released:
                    stored = await _store.ReleaseAsync(lease, outcome.Delay, CancellationToken.None).ConfigureAwait(false);
                    break;
                case RogueJobsTelemetry.Retried:
                    _logRetry(_logger, job.Id, job.Kind, job.Attempt, job.MaxAttempts, outcome.Delay, outcome.Exception);
                    stored = await _store.RetryAsync(lease, outcome.Delay, outcome.Error!, CancellationToken.None).ConfigureAwait(false);
                    break;
                default:
                    _logDeadLetter(_logger, job.Id, job.Kind, job.Attempt, outcome.Exception);
                    stored = await _store.DeadLetterAsync(lease, outcome.Error!, CancellationToken.None).ConfigureAwait(false);
                    break;
            }
            if (!stored)
            {
                _logSettleLost(_logger, job.Id, job.Kind, outcome.Name, null);
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

        /// <summary>Put back without counting the attempt, to run again after <paramref name="delay"/>.</summary>
        public static Outcome PutBack(TimeSpan delay) => new Outcome(RogueJobsTelemetry.Released, delay, null);

        public string Name { get; }

        public TimeSpan Delay { get; }

        public Exception? Exception { get; }

        /// <summary>
        /// The error recorded with the job: the exception's full type name only, because the store
        /// returns it to anyone who reads the job and the message can quote the payload or a secret.
        /// The retry and dead-letter logs keep the whole exception.
        /// </summary>
        public string? Error => Exception is null ? null : JobLimits.TrimError(Exception.GetType().FullName ?? Exception.GetType().Name);

        /// <summary>The exception's type name, the only part of it telemetry carries.</summary>
        public string? ErrorType => Exception?.GetType().Name;

        public static Outcome Retry(TimeSpan delay, Exception exception) => new Outcome(RogueJobsTelemetry.Retried, delay, exception);

        public static Outcome DeadLetter(Exception exception) => new Outcome(RogueJobsTelemetry.DeadLettered, TimeSpan.Zero, exception);
    }

    /// <summary>The service scope the jobs of one claimed batch share; the last job to leave disposes it.</summary>
    internal sealed class BatchScope
    {
        private readonly AsyncServiceScope _scope;
        private int _remaining;

        public BatchScope(AsyncServiceScope scope, int jobs)
        {
            _scope = scope;
            _remaining = jobs;
        }

        public IServiceProvider Services => _scope.ServiceProvider;

        public ValueTask LeaveAsync() => Interlocked.Decrement(ref _remaining) == 0 ? _scope.DisposeAsync() : default;
    }
}
