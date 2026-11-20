using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using FluentValidation;
using SkathIO.Rogue.Validation;

namespace SkathIO.Rogue.Benchmarks.Validation;

// ── The same order model, checked by SkathIO.Rogue.Validation (generated) and FluentValidation. ──

/// <summary>An order with a nested customer and three lines, checked by generated code.</summary>
[Validate]
public sealed partial record BenchOrder
{
    [NotEmpty, MaximumLength(50)] public string Reference { get; init; } = string.Empty;

    [GreaterThan(0)] public decimal Amount { get; init; }

    public BenchCustomer Customer { get; init; } = new BenchCustomer();

    [NotEmpty] public IReadOnlyList<BenchLine> Lines { get; init; } = Array.Empty<BenchLine>();
}

/// <summary>The customer of a <see cref="BenchOrder"/>.</summary>
[Validate]
public sealed partial record BenchCustomer
{
    [NotEmpty] public string Name { get; init; } = string.Empty;
}

/// <summary>One line of a <see cref="BenchOrder"/>.</summary>
[Validate]
public sealed partial record BenchLine
{
    [NotEmpty] public string Sku { get; init; } = string.Empty;

    [GreaterThan(0)] public int Quantity { get; init; }
}

/// <summary>FluentValidation rules equivalent to the attributes on <see cref="BenchOrder"/>.</summary>
public sealed class BenchOrderValidator : AbstractValidator<BenchOrder>
{
    public BenchOrderValidator()
    {
        RuleFor(x => x.Reference).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Customer).NotNull().SetValidator(new BenchCustomerValidator());
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).SetValidator(new BenchLineValidator());
    }
}

/// <summary>FluentValidation rules for <see cref="BenchCustomer"/>.</summary>
public sealed class BenchCustomerValidator : AbstractValidator<BenchCustomer>
{
    public BenchCustomerValidator() => RuleFor(x => x.Name).NotEmpty();
}

/// <summary>FluentValidation rules for <see cref="BenchLine"/>.</summary>
public sealed class BenchLineValidator : AbstractValidator<BenchLine>
{
    public BenchLineValidator()
    {
        RuleFor(x => x.Sku).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
    }
}

/// <summary>
/// The validation engines side by side, without dispatch: SkathIO.Rogue.Validation's generated
/// validator against FluentValidation's <c>Validate</c>, on a valid order and on one with two broken
/// lines. The generated method is the one the dispatcher calls before the handler; calling it here
/// keeps the dispatch cost out of the comparison. In-dispatch cost of the valid path is held at 0 B by
/// the generator tests.
/// </summary>
[MemoryDiagnoser]
public class InHouseValidationBenchmarks
{
    private readonly BenchOrderValidator _fluent = new BenchOrderValidator();
    private BenchOrder _valid = null!;
    private BenchOrder _invalid = null!;

    [GlobalSetup]
    public void Setup()
    {
        BenchLine[] lines = { new BenchLine { Sku = "A", Quantity = 1 }, new BenchLine { Sku = "B", Quantity = 2 }, new BenchLine { Sku = "C", Quantity = 3 } };
        _valid = new BenchOrder { Reference = "R-1", Amount = 10, Customer = new BenchCustomer { Name = "Ada" }, Lines = lines };
        _invalid = _valid with { Lines = new[] { lines[0], new BenchLine { Sku = "", Quantity = 0 }, new BenchLine { Sku = "", Quantity = 1 } } };
    }

    [Benchmark(Baseline = true)]
    public int FluentValidation_Valid() => _fluent.Validate(_valid).Errors.Count;

    [Benchmark]
    public int RogueValidation_Valid() => RogueErrors(_valid);

    [Benchmark]
    public int FluentValidation_Invalid() => _fluent.Validate(_invalid).Errors.Count;

    [Benchmark]
    public int RogueValidation_Invalid() => RogueErrors(_invalid);

    private static int RogueErrors(BenchOrder order)
    {
        var errors = default(ValidationErrors);
        BenchOrder.__RogueValidate(order, ref errors);
        return errors.Count;
    }
}
