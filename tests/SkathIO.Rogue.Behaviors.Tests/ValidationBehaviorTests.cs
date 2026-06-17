using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using SkathIO.Rogue.Validation.FluentValidation;
using Xunit;

namespace SkathIO.Rogue.Behaviors.Tests;

// Plain class (not ICommand<T>): exercises ValidationBehavior directly, so no handler is needed and
// ROGUE001 is avoided.
internal sealed class CreateUser
{
    public string Name { get; init; } = "";
    public int Age { get; init; }
}

internal sealed class CreateUserNameValidator : AbstractValidator<CreateUser>
{
    public CreateUserNameValidator()
        => RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required");
}

internal sealed class CreateUserAgeValidator : AbstractValidator<CreateUser>
{
    public CreateUserAgeValidator()
        => RuleFor(x => x.Age).GreaterThan(0).WithMessage("Age must be positive");
}

public sealed class ValidationBehaviorTests
{
    private static (RequestHandlerDelegate<string> next, System.Func<bool> ran) TrackingHandler()
    {
        bool ran = false;
        RequestHandlerDelegate<string> next = () =>
        {
            ran = true;
            return new ValueTask<string>("handler-ran");
        };
        return (next, () => ran);
    }

    [Fact]
    public async Task ValidationBehavior_ThrowsValidationException_WhenValidatorFails()
    {
        var behavior = new ValidationBehavior<CreateUser, string>(
            new IValidator<CreateUser>[] { new CreateUserNameValidator() });
        var (next, ran) = TrackingHandler();

        // Name empty → validation fails → exception before the handler runs.
        await Assert.ThrowsAsync<ValidationException>(
            () => behavior.Handle(new CreateUser { Name = "", Age = 5 }, next, CancellationToken.None).AsTask());

        Assert.False(ran(), "handler must not run when validation fails");
    }

    [Fact]
    public async Task ValidationBehavior_PassesThrough_WhenValidationSucceeds()
    {
        var behavior = new ValidationBehavior<CreateUser, string>(
            new IValidator<CreateUser>[] { new CreateUserNameValidator(), new CreateUserAgeValidator() });
        var (next, ran) = TrackingHandler();

        var result = await behavior.Handle(
            new CreateUser { Name = "Ada", Age = 42 }, next, CancellationToken.None);

        Assert.True(ran(), "handler must run when validation succeeds");
        Assert.Equal("handler-ran", result);
    }

    [Fact]
    public async Task ValidationBehavior_AggregatesAllFailures()
    {
        // Two validators, each contributing one failure for the same invalid request.
        var behavior = new ValidationBehavior<CreateUser, string>(
            new IValidator<CreateUser>[] { new CreateUserNameValidator(), new CreateUserAgeValidator() });
        var (next, _) = TrackingHandler();

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => behavior.Handle(new CreateUser { Name = "", Age = 0 }, next, CancellationToken.None).AsTask());

        Assert.Equal(2, ex.Errors.Count());
        Assert.Contains(ex.Errors, e => e.ErrorMessage == "Name is required");
        Assert.Contains(ex.Errors, e => e.ErrorMessage == "Age must be positive");
    }

    [Fact]
    public async Task ValidationBehavior_PassesThrough_WhenNoValidators()
    {
        // Zero validators registered is valid — the request flows straight to the handler.
        var behavior = new ValidationBehavior<CreateUser, string>(
            System.Array.Empty<IValidator<CreateUser>>());
        var (next, ran) = TrackingHandler();

        var result = await behavior.Handle(
            new CreateUser { Name = "", Age = 0 }, next, CancellationToken.None);

        Assert.True(ran());
        Assert.Equal("handler-ran", result);
    }
}
