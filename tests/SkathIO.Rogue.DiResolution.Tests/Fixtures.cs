using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using SkathIO.Rogue;

namespace SkathIO.Rogue.DiResolution.Tests;

// ── Test message types ──────────────────────────────────────────────────────
//
// A single command/handler/validator triple, reused across the lifetime matrix (design.md §2).
// Referencing SkathIO.Rogue.Validation.FluentValidation in this project's .csproj auto-weaves
// ValidationBehavior<,> into every request here (PD-17) — no explicit AddOpenBehavior needed.

/// <summary>Command exercised by the DI-resolution matrix tests.</summary>
public sealed class CreateUserCommand : ICommand<string>
{
    public string Name { get; init; } = "";
    public int Age { get; init; }
}

/// <summary>Handler for <see cref="CreateUserCommand"/> — returns a deterministic, checkable value.</summary>
public sealed class CreateUserHandler : ICommandHandler<CreateUserCommand, string>
{
    public ValueTask<string> Handle(CreateUserCommand request, CancellationToken cancellationToken)
        => new("created:" + request.Name);
}

/// <summary>Validator registered manually as Scoped (the <c>AddValidatorsFromAssembly</c> default
/// lifetime) — see iteration 2.1's Scope for why this is registered by hand rather than via the
/// separate <c>FluentValidation.DependencyInjectionExtensions</c> package.</summary>
public sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator()
        => RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required");
}
