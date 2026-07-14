using System;

namespace SkathIO.Rogue.Smoke.Application.Orders;

public sealed record GetOrderQuery(Guid OrderId) : IQuery<OrderDto>;

/// <summary>Thrown by <see cref="GetOrderQueryHandler"/> when no order matches — mapped to HTTP 404
/// by the host's exception-mapping middleware, the same pattern used for
/// <see cref="FluentValidation.ValidationException"/> → 400.</summary>
public sealed class OrderNotFoundException(Guid orderId) : Exception($"Order '{orderId}' not found.");
