using System;
using FluentValidation;

namespace SkathIO.Rogue.Smoke.Application.Orders;

/// <summary>
/// A second validator, deliberately added with zero corresponding change to
/// <see cref="ApplicationServiceCollectionExtensions.AddSmokeApplication"/> — proof that
/// <c>AddValidatorsFromAssemblyContaining</c> picks up every <c>IValidator&lt;T&gt;</c> in this
/// assembly, not just the first one wired in by hand.
/// </summary>
public sealed class MarkOrderShippedCommandValidator : AbstractValidator<MarkOrderShippedCommand>
{
    public MarkOrderShippedCommandValidator()
        => RuleFor(x => x.OrderId).NotEqual(Guid.Empty).WithMessage("OrderId is required");
}
