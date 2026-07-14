namespace SkathIO.Rogue.Smoke.Application.Orders;

/// <summary>Streams every stored order — the smoke test's coverage for <c>IStreamQuery&lt;T&gt;</c>.</summary>
public sealed record StreamOrdersQuery : IStreamQuery<OrderDto>;
