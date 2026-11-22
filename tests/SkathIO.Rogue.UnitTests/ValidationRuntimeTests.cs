using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue.Validation;
using Xunit;

namespace SkathIO.Rogue.UnitTests;

/// <summary>
/// The runtime types of SkathIO.Rogue.Validation: the error accumulator, the exception and its
/// problem dictionary, the async-rule runner and the built-in rules' checks.
/// </summary>
public sealed class ValidationRuntimeTests
{
    // ── ValidationErrors ─────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultErrors_AreEmpty_AndThrowNothing()
    {
        var errors = default(ValidationErrors);

        Assert.Equal(0, errors.Count);
        Assert.False(errors.HasErrors);
        Assert.Empty(errors.Items);
        errors.ThrowIfAny();
    }

    [Theory]
    [InlineData(null, "code", "message")]
    [InlineData("path", null, "message")]
    [InlineData("path", "code", null)]
    public void Add_RejectsANullPart(string? path, string? code, string? message)
    {
        var errors = default(ValidationErrors);

        Assert.Throws<ArgumentNullException>(() => errors.Add(path!, code!, message!));
        Assert.Equal(0, errors.Count);
    }

    [Theory]
    [InlineData("Sku", "Lines[2]", "Lines[2].Sku")]
    [InlineData("[0].Sku", "Matrix", "Matrix[0].Sku")]
    [InlineData("", "Lines[1]", "Lines[1]")]
    public void PrefixSince_JoinsThePrefixInPathForm(string path, string prefix, string expected)
    {
        var errors = default(ValidationErrors);
        errors.Add("Untouched", "c", "m");
        errors.Add(path, "c", "m");

        errors.PrefixSince(1, prefix);

        Assert.Equal(new[] { "Untouched", expected }, errors.Items.Select(static e => e.Path));
    }

    [Fact]
    public void PrefixSince_OnEmptyErrors_DoesNothing()
    {
        var errors = default(ValidationErrors);

        errors.PrefixSince(0, "Lines[0]");

        Assert.Equal(0, errors.Count);
    }

    // ── RogueValidationException ─────────────────────────────────────────────────────

    [Fact]
    public void ThrowIfAny_ThrowsEveryError_InOrder()
    {
        var errors = default(ValidationErrors);
        errors.Add("Reference", "not_empty", "Reference is required.");
        errors.Add("Lines[1].Sku", "not_empty", "Sku is required.");

        RogueValidationException ex = Assert.Throws<RogueValidationException>(() => errors.ThrowIfAny());

        Assert.Equal(new[] { "Reference", "Lines[1].Sku" }, ex.Errors.Select(static e => e.Path));
    }

    [Fact]
    public void ToProblemDictionary_GroupsMessagesByPath_KeepingFirstFailureOrder()
    {
        var ex = new RogueValidationException(new[]
        {
            new ValidationError("Window", "a", "First."),
            new ValidationError("Reference", "b", "Required."),
            new ValidationError("Window", "c", "Second."),
        });

        IDictionary<string, string[]> problems = ex.ToProblemDictionary();

        Assert.Equal(new[] { "Window", "Reference" }, problems.Keys);
        Assert.Equal(new[] { "First.", "Second." }, problems["Window"]);
        Assert.Equal(new[] { "Required." }, problems["Reference"]);
    }

    [Fact]
    public void ExceptionMessage_NamesThePaths_ButNoMessage()
    {
        var ex = new RogueValidationException(new[]
        {
            new ValidationError("Password", "too_short", "'hunter2' is too short."),
            new ValidationError("Password", "weak", "Weak."),
        });

        Assert.Equal("Validation failed with 2 errors: Password.", ex.Message);
    }

    [Fact]
    public void Exception_RejectsNullErrors()
    {
        Assert.Throws<ArgumentNullException>(() => new RogueValidationException(null!));
    }

    // ── AsyncRulesRunner ─────────────────────────────────────────────────────────────

    public static TheoryData<IEnumerable<IAsyncRules<string>>?> NoRules() => new()
    {
        null,
        Array.Empty<IAsyncRules<string>>(),
        new List<IAsyncRules<string>>(),
    };

    [Theory]
    [MemberData(nameof(NoRules))]
    public void CheckAsync_WithNoRule_CompletesSynchronously(IEnumerable<IAsyncRules<string>>? rules)
    {
        ValueTask pending = AsyncRulesRunner.CheckAsync(rules, "request", CancellationToken.None);

        Assert.True(pending.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CheckAsync_RunsEveryRule_ThenThrowsWithAllTheirErrors()
    {
        IEnumerable<IAsyncRules<string>> rules = Lazy(new Rule("A"), new Rule(null), new Rule("B"));

        RogueValidationException ex = await Assert.ThrowsAsync<RogueValidationException>(
            () => AsyncRulesRunner.CheckAsync(rules, "request", CancellationToken.None).AsTask());

        Assert.Equal(new[] { "A", "B" }, ex.Errors.Select(static e => e.Path));
    }

    [Fact]
    public async Task CheckAsync_WhenNoRuleAddsAnError_Completes()
    {
        await AsyncRulesRunner.CheckAsync(Lazy(new Rule(null)), "request", CancellationToken.None);
    }

    // A sequence that is not a collection, so the runner cannot see its count up front.
    private static IEnumerable<IAsyncRules<string>> Lazy(params IAsyncRules<string>[] rules)
    {
        foreach (IAsyncRules<string> rule in rules)
        {
            yield return rule;
        }
    }

    private sealed class Rule(string? path) : IAsyncRules<string>
    {
        public async ValueTask CheckAsync(string request, ValidationErrors errors, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (path is not null)
            {
                errors.Add(path, "code", "message");
            }
        }
    }

    // ── Built-in rules ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("a", true)]
    public void NotEmpty_String(string? value, bool valid) => Assert.Equal(valid, NotEmptyAttribute.IsValid(value));

    [Fact]
    public void NotEmpty_CollectionsAndGuid()
    {
        Assert.False(NotEmptyAttribute.IsValid<int>(null));
        Assert.False(NotEmptyAttribute.IsValid(new List<int>()));
        Assert.True(NotEmptyAttribute.IsValid(new[] { 1 }));
        Assert.False(NotEmptyAttribute.IsValid(Enumerable.Empty<int>().Where(static i => i > 0)));
        Assert.True(NotEmptyAttribute.IsValid(Enumerable.Range(0, 1).Where(static i => i >= 0)));
        Assert.False(NotEmptyAttribute.IsValid(Guid.Empty));
        Assert.True(NotEmptyAttribute.IsValid(Guid.NewGuid()));
    }

    [Theory]
    [InlineData("ab", 2, true)]
    [InlineData("a", 2, false)]
    [InlineData(null, 2, true)]
    public void MinimumLength_String(string? value, int length, bool valid) => Assert.Equal(valid, MinimumLengthAttribute.IsValid(value, length));

    [Theory]
    [InlineData("ab", 2, true)]
    [InlineData("abc", 2, false)]
    [InlineData(null, 2, true)]
    public void MaximumLength_String(string? value, int length, bool valid) => Assert.Equal(valid, MaximumLengthAttribute.IsValid(value, length));

    [Fact]
    public void Lengths_OnCollections_CountItems()
    {
        Assert.True(MinimumLengthAttribute.IsValid(new[] { 1, 2 }, 2));
        Assert.False(MinimumLengthAttribute.IsValid(new[] { 1 }, 2));
        Assert.True(MaximumLengthAttribute.IsValid(new[] { 1, 2 }, 2));
        Assert.False(MaximumLengthAttribute.IsValid(new[] { 1, 2, 3 }, 2));
    }

    [Fact]
    public void Comparisons_AreExclusive_AndLetNullPass()
    {
        Assert.False(GreaterThanAttribute.IsValid(0L, 0));
        Assert.True(GreaterThanAttribute.IsValid(1L, 0));
        Assert.True(GreaterThanAttribute.IsValid(0.5m, 0));
        Assert.True(GreaterThanAttribute.IsValid((long?)null, 0));
        Assert.False(LessThanAttribute.IsValid(10.0, 10));
        Assert.True(LessThanAttribute.IsValid(9.99m, 10));
        Assert.True(LessThanAttribute.IsValid((double?)null, 10));
    }
}
