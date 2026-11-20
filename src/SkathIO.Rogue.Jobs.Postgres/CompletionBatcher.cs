using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SkathIO.Rogue.Jobs.Postgres;

/// <summary>
/// Joins concurrent completions into one statement. A completion that arrives while none is being
/// written is written at once, so a lone job never waits; completions that arrive during a write are
/// written together by the next one. Each caller gets the count of its own leases the store still held.
/// </summary>
internal sealed class CompletionBatcher
{
    private readonly Func<IReadOnlyList<JobLease>, Task<HashSet<JobLease>>> _write;
    private readonly object _gate = new object();
    private List<Pending> _waiting = new List<Pending>();
    private bool _writing;

    /// <param name="write">Completes the leases and returns those the store still held.</param>
    public CompletionBatcher(Func<IReadOnlyList<JobLease>, Task<HashSet<JobLease>>> write)
    {
        _write = write;
    }

    public async ValueTask<int> CompleteAsync(IReadOnlyList<JobLease> leases, CancellationToken cancellationToken)
    {
        var pending = new Pending(leases);
        bool writer;
        lock (_gate)
        {
            _waiting.Add(pending);
            writer = !_writing;
            _writing = true;
        }

        if (writer)
        {
            await WriteUntilDrainedAsync().ConfigureAwait(false);
        }
        return await pending.Done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Writes batch after batch until no completion is waiting. Only one caller runs this at a time.
    private async Task WriteUntilDrainedAsync()
    {
        while (true)
        {
            List<Pending> batch;
            lock (_gate)
            {
                if (_waiting.Count == 0)
                {
                    _writing = false;
                    return;
                }
                batch = _waiting;
                _waiting = new List<Pending>();
            }
            await WriteBatchAsync(batch).ConfigureAwait(false);
        }
    }

    private async Task WriteBatchAsync(List<Pending> batch)
    {
        var leases = new List<JobLease>();
        foreach (Pending pending in batch)
        {
            leases.AddRange(pending.Leases);
        }

        HashSet<JobLease> held;
        try
        {
            held = await _write(leases).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            foreach (Pending pending in batch)
            {
                pending.Done.TrySetException(ex);
            }
            return;
        }

        // A lease counts once, for the first caller that named it, as the in-memory store counts it.
        foreach (Pending pending in batch)
        {
            int count = 0;
            foreach (JobLease lease in pending.Leases)
            {
                if (held.Remove(lease))
                {
                    count++;
                }
            }
            pending.Done.TrySetResult(count);
        }
    }

    private sealed class Pending
    {
        public Pending(IReadOnlyList<JobLease> leases)
        {
            Leases = leases;
        }

        public IReadOnlyList<JobLease> Leases { get; }

        public TaskCompletionSource<int> Done { get; } = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
