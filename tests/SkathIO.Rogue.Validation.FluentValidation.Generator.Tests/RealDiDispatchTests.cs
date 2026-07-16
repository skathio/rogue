using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Validation.FluentValidation.Generator.Tests;

/// <summary>
/// Real-DI, zero-explicit-registration proof (R1/R3, D5's fully-implicit discovery) plus the D4/NF4
/// Scoped-lifetime pin — builds an actual <see cref="IServiceCollection"/>/<see cref="IServiceProvider"/>
/// with <c>ValidateScopes</c>/<c>ValidateOnBuild</c>, mirroring
/// <c>SkathIO.Rogue.DiResolution.Tests</c>' container-boundary strictness. There is no explicit
/// registration call to omit (D5 removed the only candidate); every test here calls plain
/// <c>AddRogue()</c> and nothing else.
/// <para>
/// Only this one class in the project touches the process-global
/// <see cref="SkathIO.Rogue.RogueRegistrationBridge"/> (via
/// <see cref="GeneratorTestHelper.BuildProviderFromGenerated"/>, which itself snapshots/resets/restores
/// around each call). xUnit runs test methods within a single class sequentially by default, so — unlike
/// the core generator test suite's <c>RealDiDispatchCollection</c> — no cross-class collection guard is
/// needed here yet. Add one if a second bridge-touching class is ever introduced in this project.
/// </para>
/// </summary>
public sealed class RealDiDispatchTests
{
    private const string SingleValidatorCommandSource = @"
using FluentValidation;

public class CreateOrderCommand
{
    public string Name { get; set; } = """";
}

public class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator() => RuleFor(x => x.Name).NotEmpty();
}";

    private const string TwoValidatorCommandSource = @"
using FluentValidation;

public class CreateOrderCommand
{
    public string Name { get; set; } = """";
}

public class NameNotEmptyValidator : AbstractValidator<CreateOrderCommand>
{
    public NameNotEmptyValidator() => RuleFor(x => x.Name).NotEmpty();
}

public class NameMaxLengthValidator : AbstractValidator<CreateOrderCommand>
{
    public NameMaxLengthValidator() => RuleFor(x => x.Name).MaximumLength(3);
}";

    /// <summary>
    /// Resolves <c>IEnumerable&lt;IValidator&lt;TRequest&gt;&gt;</c> for a runtime-loaded request type
    /// via plain reflection — this test project has no compile-time reference to the dynamically
    /// compiled fixture assembly's own types (<c>CreateOrderCommand</c> et al.).
    /// </summary>
    private static List<object> ResolveValidators(IServiceProvider scopedProvider, Type requestType)
    {
        Type closedValidatorIface = typeof(IValidator<>).MakeGenericType(requestType);
        Type enumerableType = typeof(IEnumerable<>).MakeGenericType(closedValidatorIface);
        var raw = (IEnumerable)scopedProvider.GetService(enumerableType)!;
        return raw.Cast<object>().ToList();
    }

    /// <summary>
    /// Builds a <c>FluentValidation.ValidationContext&lt;TRequest&gt;</c> via reflection (same reason
    /// as <see cref="ResolveValidators"/>) and runs <c>Validate</c> through the non-generic
    /// <see cref="IValidator"/> interface — proving the resolved instance's rule genuinely executes,
    /// not merely that DI resolution succeeded.
    /// </summary>
    private static ValidationResult Validate(object validator, Type requestType, object request)
    {
        Type closedContextType = typeof(ValidationContext<>).MakeGenericType(requestType);
        var context = (IValidationContext)Activator.CreateInstance(closedContextType, request)!;
        return ((IValidator)validator).Validate(context);
    }

    [Fact]
    public void AddRogue_NoExplicitRegistrationOfAnyKind_DiscoveredValidatorFires()
    {
        var assembly = GeneratorTestHelper.EmitAndLoadAssembly(SingleValidatorCommandSource);
        var (services, provider) = GeneratorTestHelper.BuildProviderFromGenerated(assembly);

        var requestType = assembly.GetType("CreateOrderCommand", throwOnError: true)!;
        var closedValidatorIface = typeof(IValidator<>).MakeGenericType(requestType);

        // R3/D5: the only action taken was plain AddRogue() inside BuildProviderFromGenerated — no
        // hand-written IValidator<T> registration exists anywhere, yet the descriptor is present.
        Assert.Contains(services, d => d.ServiceType == closedValidatorIface);

        using var scope = provider.CreateScope();
        var validators = ResolveValidators(scope.ServiceProvider, requestType);
        Assert.Single(validators);

        var emptyNameRequest = Activator.CreateInstance(requestType)!; // Name defaults to ""
        var result = Validate(validators[0], requestType, emptyNameRequest);

        // The rule genuinely executed — empty Name fails NotEmpty. Proves the validator "fires", not
        // just that it resolves.
        Assert.False(result.IsValid);
    }

    [Fact]
    public void AddRogue_NoExplicitRegistrationOfAnyKind_ValidatorPassesForValidInput()
    {
        var assembly = GeneratorTestHelper.EmitAndLoadAssembly(SingleValidatorCommandSource);
        var (_, provider) = GeneratorTestHelper.BuildProviderFromGenerated(assembly);

        var requestType = assembly.GetType("CreateOrderCommand", throwOnError: true)!;

        using var scope = provider.CreateScope();
        var validators = ResolveValidators(scope.ServiceProvider, requestType);
        Assert.Single(validators);

        var request = Activator.CreateInstance(requestType)!;
        requestType.GetProperty("Name")!.SetValue(request, "Ada");
        var result = Validate(validators[0], requestType, request);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void TwoValidators_ForSameRequestType_BothFireIndependently()
    {
        // R5/D4: TryAddEnumerable means two validators for one request type is a legitimate,
        // supported shape — proves both actually run (not just that two descriptors exist).
        var assembly = GeneratorTestHelper.EmitAndLoadAssembly(TwoValidatorCommandSource);
        var (_, provider) = GeneratorTestHelper.BuildProviderFromGenerated(assembly);

        var requestType = assembly.GetType("CreateOrderCommand", throwOnError: true)!;

        using var scope = provider.CreateScope();
        var validators = ResolveValidators(scope.ServiceProvider, requestType);
        Assert.Equal(2, validators.Count);

        var request = Activator.CreateInstance(requestType)!;
        requestType.GetProperty("Name")!.SetValue(request, "TooLongAName"); // non-empty, > 3 chars

        var results = validators.Select(v => Validate(v, requestType, request)).ToList();

        // NameNotEmptyValidator passes (non-empty); NameMaxLengthValidator fails (> 3 chars) — both
        // independently evaluated the same request.
        Assert.Contains(results, r => r.IsValid);
        Assert.Contains(results, r => !r.IsValid);
    }

    [Fact]
    public void ValidatorRegistration_IsScoped_EvenWhenRogueOptionsLifetimeIsSingleton()
    {
        var assembly = GeneratorTestHelper.EmitAndLoadAssembly(SingleValidatorCommandSource);
        var (services, provider) = GeneratorTestHelper.BuildProviderFromGenerated(
            assembly, configure: o => o.Lifetime = ServiceLifetime.Singleton);

        var requestType = assembly.GetType("CreateOrderCommand", throwOnError: true)!;
        var closedValidatorIface = typeof(IValidator<>).MakeGenericType(requestType);

        // D4/NF4: hard-pinned Scoped, decoupled from RogueOptions.Lifetime. Anti-vacuous-pass check
        // (mirrors DiResolutionMatrixTests' own pattern): assert the REGISTERED lifetime directly
        // rather than trusting the configuration alone.
        var descriptor = services.Single(d => d.ServiceType == closedValidatorIface);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);

        // Still resolves cleanly under ValidateScopes/ValidateOnBuild despite the Singleton-configured
        // host lifetime — proves no captive-dependency trap was introduced.
        using var scope = provider.CreateScope();
        var validators = ResolveValidators(scope.ServiceProvider, requestType);
        Assert.Single(validators);
    }
}
