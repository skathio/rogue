using System;
using System.Collections.Generic;

namespace SkathIO.Rogue.Messaging.RabbitMQ;

/// <summary>
/// The delivery counts of the copies this transport has sent back to its own queue for another
/// attempt, keyed by message name and id. A delivery's count comes from here and from the broker's
/// redelivered flag, never from a header: a sender could set any header, and a high count would switch
/// off the retries of its messages. The count stamped on a copy is only for whoever reads the queue.
/// </summary>
/// <remarks>
/// The counts live in this process. A copy taken by another instance of the service, or read after a
/// restart, starts again from 1, so a message can get more attempts than the limit, never fewer. The
/// counts are kept in two generations of at most <see cref="GenerationSize"/> each: when the newer one
/// is full the older is dropped, so copies that never come back cannot grow the memory without bound.
/// </remarks>
internal sealed class DeliveryCounts
{
    /// <summary>The most counts one generation holds.</summary>
    public const int GenerationSize = 10_000;

    private readonly object _gate = new object();
    private readonly int _generationSize;
    private Dictionary<(string Name, string Id), int> _current = new Dictionary<(string Name, string Id), int>();
    private Dictionary<(string Name, string Id), int> _previous = new Dictionary<(string Name, string Id), int>();

    public DeliveryCounts(int generationSize = GenerationSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generationSize, 1);
        _generationSize = generationSize;
    }

    /// <summary>
    /// How many times a delivery of the message has been delivered: the count recorded when this
    /// transport sent it back (1 when there is none), one more when the broker marks it redelivered
    /// after a delivery that was never settled.
    /// </summary>
    public int Of((string Name, string Id) key, bool redelivered)
    {
        int count;
        lock (_gate)
        {
            count = Find(key) ?? 1;
        }
        return Math.Clamp(count, 1, RabbitMqWire.MaxDeliveryCount) + (redelivered ? 1 : 0);
    }

    /// <summary>Records the count the next delivery of the message has, and returns the one it replaces.</summary>
    public int? Record((string Name, string Id) key, int count)
    {
        lock (_gate)
        {
            int? replaced = Find(key);
            _previous.Remove(key);
            if (!_current.ContainsKey(key) && _current.Count >= _generationSize)
            {
                _previous = _current;
                _current = new Dictionary<(string Name, string Id), int>();
            }
            _current[key] = count;
            return replaced;
        }
    }

    /// <summary>Puts back the count <see cref="Record"/> replaced: <paramref name="count"/>, or none.</summary>
    public void Restore((string Name, string Id) key, int? count)
    {
        if (count is int previous)
        {
            Record(key, previous);
        }
        else
        {
            Forget(key);
        }
    }

    /// <summary>Drops the count of a message that is settled for good.</summary>
    public void Forget((string Name, string Id) key)
    {
        lock (_gate)
        {
            _current.Remove(key);
            _previous.Remove(key);
        }
    }

    // Called under the gate.
    private int? Find((string Name, string Id) key)
    {
        if (_current.TryGetValue(key, out int count) || _previous.TryGetValue(key, out count))
        {
            return count;
        }
        return null;
    }
}
