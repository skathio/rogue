namespace SkathIO.Rogue.Validation.FluentValidation.SourceGenerator;

/// <summary>
/// A discovered concrete <c>FluentValidation.IValidator&lt;T&gt;</c> implementor (which also catches
/// <c>AbstractValidator&lt;T&gt;</c> subclasses, since <c>AbstractValidator&lt;T&gt; : IValidator&lt;T&gt;</c>).
/// <see cref="IsAbstract"/>/<see cref="HasPublicCtor"/> are computed unconditionally by the discovery
/// transform (mirroring <c>RogueGenerator.ExtractBehaviorFromMetadataSymbol</c>'s checks); a candidate
/// with either flag set is dropped — silently, no diagnostic (spec.md §4 non-goal) — when the final
/// <see cref="DiscoveredValidators"/> set is built, not at the point of extraction.
/// </summary>
internal sealed record ValidatorModel(
    string TypeFqn,
    string RequestFqn,
    bool IsAbstract,
    bool HasPublicCtor);

/// <summary>All validators discovered from one compilation's source (D2 — source-only, no metadata scan).</summary>
internal sealed record DiscoveredValidators(EquatableArray<ValidatorModel> Validators);
