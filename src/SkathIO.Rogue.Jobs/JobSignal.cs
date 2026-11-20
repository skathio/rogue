using System;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs;

/// <summary>
/// Wakes an idle worker in this process when a job is enqueued, so it does not wait out its poll
/// interval. Polling still finds jobs enqueued by other processes.
/// </summary>
internal sealed class JobSignal
{
    private readonly SemaphoreSlim _pulse = new SemaphoreSlim(0, 1);

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
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _pulse.WaitAsync(timeout, cancellationToken);
}
