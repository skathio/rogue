using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SkathIO.Rogue.Smoke.Infrastructure;

/// <summary>Process-lifetime, in-memory <see cref="IOrderStore"/> — registered Singleton by the host
/// so state survives across requests within a single test run.</summary>
public sealed class InMemoryOrderStore : IOrderStore
{
    private readonly ConcurrentDictionary<Guid, OrderRecord> _orders = new();

    public OrderRecord Add(string productId, int quantity)
    {
        var record = new OrderRecord(Guid.NewGuid(), productId, quantity, Shipped: false);
        _orders[record.Id] = record;
        return record;
    }

    public OrderRecord? Get(Guid id) => _orders.TryGetValue(id, out OrderRecord? record) ? record : null;

    public OrderRecord MarkShipped(Guid id)
    {
        if (!_orders.TryGetValue(id, out OrderRecord? existing))
        {
            throw new KeyNotFoundException($"Order '{id}' not found.");
        }

        OrderRecord shipped = existing with { Shipped = true };
        _orders[id] = shipped;
        return shipped;
    }

    public IReadOnlyCollection<OrderRecord> GetAll() => _orders.Values.ToArray();
}
