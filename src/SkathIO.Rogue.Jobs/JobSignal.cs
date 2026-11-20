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

    // Built only by AddRogueJobs: one signal per container, shared by the scheduler and the worker.
    internal JobSignal()
    {
    }

    /// <summary>Wakes the idle worker, if any; several pulses before it wakes count as one.</summary>
    public void Pulse()
    {
        try
        {
            _pulse.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending; one is enough.
        }
    }

    /// <summary>Waits for a pulse or for <paramref name="timeout"/>, whichever comes first.</summary>
    internal Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _pulse.WaitAsync(timeout, cancellationToken);
}
