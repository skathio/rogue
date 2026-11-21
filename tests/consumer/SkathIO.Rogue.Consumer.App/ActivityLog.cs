using System.Collections.Generic;

namespace SkathIO.Rogue.Consumer.App;

/// <summary>What the handlers and behaviors did, in order, for the tests to read.</summary>
public sealed class ActivityLog
{
    private readonly List<string> _entries = new List<string>();
    private readonly object _gate = new object();

    public void Record(string entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<string> Entries()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}
