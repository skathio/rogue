using System;
using FluentValidation;

namespace SkathIO.Rogue.Smoke.Application.Orders;

/// <summary>
/// A second validator, deliberately added with zero corresponding change to
/// <see cref="ApplicationServiceCollectionExtensions.AddSmokeApplication"/> — proof that the
/// FluentValidation source generator (<c>SkathIO.Rogue.Validation.FluentValidation.SourceGenerator</c>)
/// discovers and registers every <c>IValidator&lt;T&gt;</c> declared in this project's own source at
/// compile time, not just the first one wired in by hand.
/// </summary>
public sealed class MarkOrderShippedCommandValidator : AbstractValidator<MarkOrderShippedCommand>
{
    public MarkOrderShippedCommandValidator()
        => RuleFor(x => x.OrderId).NotEqual(Guid.Empty).WithMessage("OrderId is required");
}
