using System;
using System.Collections.Generic;

namespace SkathIO.Rogue.Smoke.Infrastructure;

/// <summary>A minimal order repository — stands in for a real database/ORM in this smoke test.</summary>
public interface IOrderStore
{
    OrderRecord Add(string productId, int quantity);

    OrderRecord? Get(Guid id);

    OrderRecord MarkShipped(Guid id);

    IReadOnlyCollection<OrderRecord> GetAll();
}
