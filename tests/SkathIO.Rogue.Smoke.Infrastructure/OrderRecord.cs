using System;

namespace SkathIO.Rogue.Smoke.Infrastructure;

/// <summary>The persisted shape of an order, as the infrastructure layer stores it.</summary>
public sealed record OrderRecord(Guid Id, string ProductId, int Quantity, bool Shipped);
