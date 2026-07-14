using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SkathIO.Rogue.Smoke.Infrastructure;

/// <summary>Singleton, process-lifetime <see cref="IOrderActivityLog"/>.</summary>
public sealed class InMemoryOrderActivityLog : IOrderActivityLog
{
    private readonly ConcurrentQueue<string> _entries = new();

    public void Record(string entry) => _entries.Enqueue(entry);

    public IReadOnlyList<string> Entries => _entries.ToArray();

    public void Clear() => _entries.Clear();
}
