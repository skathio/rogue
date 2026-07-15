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
        Assert.Contains("CreateOrderCommand", validator.RequestFqn);
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
}
