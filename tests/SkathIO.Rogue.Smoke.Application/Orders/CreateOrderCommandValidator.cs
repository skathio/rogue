using FluentValidation;

namespace SkathIO.Rogue.Smoke.Application.Orders;

public sealed class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty().WithMessage("ProductId is required");
        RuleFor(x => x.Quantity).GreaterThan(0).WithMessage("Quantity must be positive");
    }
}
