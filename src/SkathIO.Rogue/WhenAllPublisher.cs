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

        // Run all tasks to completion without propagating exceptions yet.
        // ContinueWith wraps each faulted task in a successful task so WhenAll completes.
        var completionTasks = new Task[tasks.Count];
        for (int i = 0; i < tasks.Count; i++)
        {
            completionTasks[i] = tasks[i].ContinueWith(static _ => { }, TaskContinuationOptions.ExecuteSynchronously);
        }
        await Task.WhenAll(completionTasks).ConfigureAwait(false);

        // Collect all exceptions from faulted tasks (FR-29 E2).
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
