using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue;

/// <summary>
/// Publishes notifications concurrently. All handlers run; exceptions from multiple handlers
/// are aggregated into an <see cref="System.AggregateException"/> (FR-29 E2).
/// </summary>
public sealed class WhenAllPublisher : INotificationPublisher
{
    /// <inheritdoc/>
#if NETSTANDARD2_0
    public async ValueTask<Unit> Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        await PublishCore(handlerExecutors, notification, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
#else
    public System.Threading.Tasks.ValueTask Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
        => new System.Threading.Tasks.ValueTask(PublishCore(handlerExecutors, notification, cancellationToken));
#endif

    private static async Task PublishCore(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        foreach (var executor in handlerExecutors)
        {
            // Capture synchronous throws as faulted Tasks so WhenAll sees them all.
            Task t;
            try { t = executor.ExecuteAsync(notification, cancellationToken).AsTask(); }
            catch (System.Exception ex) { t = Task.FromException(ex); }
            tasks.Add(t);
        }

        if (tasks.Count == 0)
        {
            return;
        }

        // Task.WhenAll always waits for every supplied task to complete — faulted or not —
        // before its own task completes, so "wait for all" holds without the prior
        // ContinueWith double-pass (which captured TaskScheduler.Current, a CA2008 hazard).
        // `await` only re-throws the *first* faulted task's exception, so swallow that and
        // collect every faulted task's exceptions ourselves (FR-29 E2) — by the time WhenAll
        // completes (faulted or not), every antecedent task is already in a terminal state.
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return;
        }
        catch (System.Exception)
        {
            // Fall through to aggregate every handler's failure below.
        }

        var exceptions = new List<System.Exception>();
        foreach (var task in tasks)
        {
            if (task.IsFaulted && task.Exception != null)
            {
                foreach (var inner in task.Exception.InnerExceptions)
                {
                    exceptions.Add(inner);
                }
            }
        }
        if (exceptions.Count > 0)
        {
            throw new System.AggregateException(exceptions);
        }
    }
}
