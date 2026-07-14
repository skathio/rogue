using System;
using SkathIO.Rogue.Smoke.Infrastructure;

namespace SkathIO.Rogue.Smoke.Application.Orders;

public sealed record OrderDto(Guid Id, string ProductId, int Quantity, bool Shipped)
{
    public static OrderDto FromRecord(OrderRecord record)
        => new(record.Id, record.ProductId, record.Quantity, record.Shipped);
}
