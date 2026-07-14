namespace SkathIO.Rogue.Smoke.Application.Orders;

/// <summary>Creates an order. Validated by <see cref="CreateOrderCommandValidator"/> (FluentValidation
/// behavior, auto-woven — see this project's csproj) and publishes <see cref="OrderPlacedEvent"/> on
/// success.</summary>
public sealed record CreateOrderCommand(string ProductId, int Quantity) : ICommand<OrderId>;
