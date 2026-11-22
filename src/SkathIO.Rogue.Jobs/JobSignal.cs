using System;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// Wakes an idle worker in this process when a job is enqueued, so it does not wait out its poll
/// interval. Polling still finds jobs enqueued by other processes, unless a store's own
/// notification (such as Postgres LISTEN) pulses this signal first.
/// </summary>
public sealed class JobSignal
{
    private readonly SemaphoreSlim _pulse = new SemaphoreSlim(0, 1);

    // Only Pulse raises the count, and only under this lock, so the count it reads cannot reach the
    // maximum before its Release: the release never throws SemaphoreFullException. A pulse that finds
    // a wake-up pending skips its release; the waiter that takes that wake-up has not yet looked for
    // jobs, so it still finds the job this pulse announces.
    private readonly object _gate = new object();

    // Built only by AddRogueJobs: one signal per container, shared by the scheduler and the worker.
    internal JobSignal()
    {
    }

    /// <summary>Wakes the idle worker, if any; several pulses before it wakes count as one.</summary>
    public void Pulse()
    {
        lock (_gate)
        {
            if (_pulse.CurrentCount == 0)
            {
                _pulse.Release();
            }
        }
    }

    /// <summary>Waits for a pulse or for <paramref name="timeout"/>, whichever comes first.</summary>
    internal Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _pulse.WaitAsync(timeout, cancellationToken);
}
