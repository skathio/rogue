using System.Linq;
using SkathIO.Rogue.Validation.FluentValidation.SourceGenerator;
using Xunit;

namespace SkathIO.Rogue.Validation.FluentValidation.Generator.Tests;

public sealed class DiscoveryTests
{
    [Fact]
    public void Validator_ImplementingAbstractValidator_IsDiscovered()
    {
        const string source = @"
using FluentValidation;

public class CreateOrderCommand
{
    public string Name { get; set; } = """";
}

public class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator()
        => RuleFor(x => x.Name).NotEmpty();
}
";
        DiscoveredValidators models = GeneratorTestHelper.ExtractModels(source);

        Assert.Single(models.Validators);
        ValidatorModel validator = models.Validators[0];
        Assert.Equal("CreateOrderCommandValidator", validator.TypeFqn.Split('.').Last());
        // F-3: exact-match assertion (not Assert.Contains) — global-namespace fixture, so the FQN is
        // just the bare type name.
        Assert.Equal("CreateOrderCommand", validator.RequestFqn);
        Assert.False(validator.IsAbstract);
        Assert.True(validator.HasPublicCtor);
    }

    // F-3: at least one fixture in a non-global namespace, exercising the namespaced-FQN path
    // Iteration 1.2's emission depends on (RegistrationEmitter reads RequestFqn/TypeFqn as-is).
    [Fact]
    public void Validator_InNamespace_HasNamespacedFqn()
    {
        const string source = @"
using FluentValidation;

namespace MyApp.Orders
{
    public class CreateOrderCommand
    {
        public string Name { get; set; } = """";
    }

    public class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
    {
        public CreateOrderCommandValidator()
            => RuleFor(x => x.Name).NotEmpty();
    }
}
";
        DiscoveredValidators models = GeneratorTestHelper.ExtractModels(source);

        Assert.Single(models.Validators);
        ValidatorModel validator = models.Validators[0];
        Assert.Equal("MyApp.Orders.CreateOrderCommandValidator", validator.TypeFqn);
        Assert.Equal("MyApp.Orders.CreateOrderCommand", validator.RequestFqn);
    }

    // F-2: a fixture implementing IValidator<T> directly (not via AbstractValidator<T>) — the
    // matching code keys on FluentValidation.IValidator`1 in AllInterfaces, which a bare
    // `class Foo : IValidator<Bar>` also hits; this path was previously untested.
    [Fact]
    public void Validator_ImplementingIValidatorDirectly_IsDiscovered()
    {
        const string source = @"
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using FluentValidation.Results;

public class Ping { }

public class PingValidator : IValidator<Ping>
{
    public ValidationResult Validate(Ping instance) => new ValidationResult();
    public ValidationResult Validate(IValidationContext context) => new ValidationResult();
    public Task<ValidationResult> ValidateAsync(Ping instance, CancellationToken cancellation = default)
        => Task.FromResult(new ValidationResult());
    public Task<ValidationResult> ValidateAsync(IValidationContext context, CancellationToken cancellation = default)
        => Task.FromResult(new ValidationResult());
    public IValidatorDescriptor CreateDescriptor() => null!;
    public bool CanValidateInstancesOfType(System.Type type) => type == typeof(Ping);
}
";
        DiscoveredValidators models = GeneratorTestHelper.ExtractModels(source);

        Assert.Single(models.Validators);
        ValidatorModel validator = models.Validators[0];
        Assert.Equal("PingValidator", validator.TypeFqn);
        Assert.Equal("Ping", validator.RequestFqn);
        Assert.False(validator.IsAbstract);
        Assert.True(validator.HasPublicCtor);
    }

    [Fact]
    public void TwoValidators_ForSameRequestType_AreBothDiscovered()
    {
        const string source = @"
using FluentValidation;

public class CreateOrderCommand
{
    public string Name { get; set; } = """";
}

public class CreateOrderCommandValidatorA : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidatorA()
        => RuleFor(x => x.Name).NotEmpty();
}

public class CreateOrderCommandValidatorB : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidatorB()
        => RuleFor(x => x.Name).MaximumLength(100);
}
";
        DiscoveredValidators models = GeneratorTestHelper.ExtractModels(source);

        Assert.Equal(2, models.Validators.Count);
        Assert.All(models.Validators, v => Assert.Contains("CreateOrderCommand", v.RequestFqn));
        Assert.Contains(models.Validators, v => v.TypeFqn.EndsWith("CreateOrderCommandValidatorA"));
        Assert.Contains(models.Validators, v => v.TypeFqn.EndsWith("CreateOrderCommandValidatorB"));
    }

    [Fact]
    public void AbstractValidator_IsExcluded()
    {
        const string source = @"
using FluentValidation;

public class Ping { }

public abstract class PingValidatorBase : AbstractValidator<Ping>
{
}
";
        DiscoveredValidators models = GeneratorTestHelper.ExtractModels(source);

        Assert.Empty(models.Validators);
    }

    [Fact]
    public void ValidatorWithNoPublicConstructor_IsExcluded()
    {
        const string source = @"
using FluentValidation;

public class Ping { }

public class PingValidator : AbstractValidator<Ping>
{
    private PingValidator() { }
}
";
        DiscoveredValidators models = GeneratorTestHelper.ExtractModels(source);

        Assert.Empty(models.Validators);
    }

    // F-4: a non-abstract class with only a PROTECTED ctor — distinct from the private-ctor case
    // above. The underlying logic (HasPublicConstructor) is the same check, but this closes the test
    // gap for the specific non-abstract-but-protected-ctor combination.
    [Fact]
    public void ValidatorWithOnlyProtectedConstructor_IsExcluded()
    {
        const string source = @"
using FluentValidation;

public class Ping { }

public class PingValidator : AbstractValidator<Ping>
{
    protected PingValidator() { }
}
";
        DiscoveredValidators models = GeneratorTestHelper.ExtractModels(source);

        Assert.Empty(models.Validators);
    }

    [Fact]
    public void OpenGenericValidator_IsExcluded()
    {
        const string source = @"
using FluentValidation;

public class GenericValidator<T> : AbstractValidator<T>
{
}
";
        DiscoveredValidators models = GeneratorTestHelper.ExtractModels(source);

        Assert.Empty(models.Validators);
    }

    [Fact]
    public void ZeroValidators_DoesNotCrash()
    {
        const string source = @"
public class PlainService
{
    public void DoSomething() { }
}
";
        DiscoveredValidators models = GeneratorTestHelper.ExtractModels(source);

        Assert.Empty(models.Validators);
    }

    [Fact]
    public void PlainClass_WithNoValidatorInterface_IsNotDiscovered()
    {
        const string source = @"
public class Base { }
public class Derived : Base { }
";
        DiscoveredValidators models = GeneratorTestHelper.ExtractModels(source);

        Assert.Empty(models.Validators);
    }

    [Fact]
    public void RunGenerator_MixedCandidates_DoesNotThrow()
    {
        const string source = @"
using FluentValidation;

public class CreateOrderCommand
{
    public string Name { get; set; } = """";
}

public class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator()
        => RuleFor(x => x.Name).NotEmpty();
}

public abstract class AbstractOnly : AbstractValidator<CreateOrderCommand>
{
}

public class NoPublicCtor : AbstractValidator<CreateOrderCommand>
{
    private NoPublicCtor() { }
}

public class OpenGeneric<T> : AbstractValidator<T>
{
}
";
        GeneratorTestHelper.RunGeneratorAndAssertClean(source);
    }

    // F-1: the mixed-candidates acceptance criterion ("a compilation with a mix of
    // valid/abstract/no-ctor/open-generic candidate types produces exactly the expected
    // ValidatorModel set") was previously only asserted as "does not throw" — this variant asserts
    // the actual survivor set. Kept alongside RunGenerator_MixedCandidates_DoesNotThrow above (that
    // test checks the full-driver path never throws; this one checks the discovery-model content —
    // different concerns, both worth keeping).
    [Fact]
    public void ExtractModels_MixedCandidates_OnlyValidSurvivorDiscovered()
    {
        const string source = @"
using FluentValidation;

public class CreateOrderCommand
{
    public string Name { get; set; } = """";
}

public class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator()
        => RuleFor(x => x.Name).NotEmpty();
}

public abstract class AbstractOnly : AbstractValidator<CreateOrderCommand>
{
}

public class NoPublicCtor : AbstractValidator<CreateOrderCommand>
{
    private NoPublicCtor() { }
}

public class OpenGeneric<T> : AbstractValidator<T>
{
}
";
        DiscoveredValidators models = GeneratorTestHelper.ExtractModels(source);

        Assert.Single(models.Validators);
        Assert.Equal("CreateOrderCommandValidator", models.Validators[0].TypeFqn);
    }
}
