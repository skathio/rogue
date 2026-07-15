using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkathIO.Rogue.Validation.FluentValidation.SourceGenerator;

[Generator]
internal sealed class RogueFluentValidationGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Stage 1: cheap syntax filter — any type declaration with a non-empty base list.
        // Stage 2: semantic transform — extract ValidatorModel records (no ISymbol/Compilation
        // survives). Mirrors RogueGenerator.cs:18-22's shape exactly.
        IncrementalValuesProvider<ValidatorModel?> candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsTypeDeclWithBaseList(node),
                transform: static (ctx, ct) => ExtractModel(ctx, ct))
            .Where(static m => m is not null);

        IncrementalValueProvider<ImmutableArray<ValidatorModel?>> allModels = candidates.Collect();

        IncrementalValueProvider<DiscoveredValidators> discovered = allModels
            .Select(static (items, _) => BuildDiscoveredValidators(items));

        // No emission yet — Iteration 1.2 replaces this no-op with the registration + module-init
        // RegisterSourceOutput calls. Kept wired (rather than omitted) so the discovery stages above
        // are exercised by the driver, not just the ExtractFromCompilation test seam.
        context.RegisterSourceOutput(discovered, static (_, _) => { });
    }

    // ─── Syntax predicate ───────────────────────────────────────────────────────────

    internal static bool IsTypeDeclWithBaseList(SyntaxNode node)
        => node is TypeDeclarationSyntax { BaseList: { Types.Count: > 0 } };

    // ─── Semantic transform ─────────────────────────────────────────────────────────

    internal static ValidatorModel? ExtractModel(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not TypeDeclarationSyntax typeDecl)
            return null;

        if (ctx.SemanticModel.GetDeclaredSymbol(typeDecl, ct) is not INamedTypeSymbol typeSymbol)
            return null;

        return ExtractFromSymbol(typeSymbol, ct);
    }

    /// <summary>
    /// Matches a concrete, non-open-generic type symbol against
    /// <c>FluentValidation.IValidator&lt;T&gt;</c> and produces the corresponding model. Shared by the
    /// incremental transform and the test seam so neither has to reconstruct a
    /// <see cref="GeneratorSyntaxContext"/>.
    /// </summary>
    internal static ValidatorModel? ExtractFromSymbol(INamedTypeSymbol typeSymbol, CancellationToken ct)
    {
        if (typeSymbol.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            return null;

        // Open-generic validators (class MyValidator<T> : AbstractValidator<T>) are explicitly
        // excluded — not a common FluentValidation pattern, out of scope for v1 (spec.md §4). This is
        // a filter in the transform (not a downstream concern), unlike the abstract/no-public-ctor
        // filter below, which BuildDiscoveredValidators applies after discovery.
        if (typeSymbol.TypeParameters.Length > 0)
            return null;

        foreach (INamedTypeSymbol iface in typeSymbol.AllInterfaces)
        {
            ct.ThrowIfCancellationRequested();

            string ifaceName = GetMetadataFqn(iface.OriginalDefinition);
            if (ifaceName == WellKnownTypeNames.IValidator1 && iface.TypeArguments.Length == 1)
            {
                return new ValidatorModel(
                    TypeFqn: GetFqn(typeSymbol),
                    RequestFqn: GetFqn(iface.TypeArguments[0]),
                    IsAbstract: typeSymbol.IsAbstract,
                    HasPublicCtor: HasPublicConstructor(typeSymbol));
            }
        }

        return null;
    }

    // ─── Stage 3: build final model ─────────────────────────────────────────────────

    /// <summary>
    /// Builds the final <see cref="DiscoveredValidators"/> from the candidate array, dropping any
    /// abstract or no-public-ctor candidate. Silent exclusion, no diagnostic (spec.md §4 non-goal) —
    /// <c>IEnumerable&lt;IValidator&lt;T&gt;&gt;</c> already tolerates a validator simply not being
    /// registered.
    /// </summary>
    internal static DiscoveredValidators BuildDiscoveredValidators(ImmutableArray<ValidatorModel?> items)
    {
        ImmutableArray<ValidatorModel>.Builder validators = ImmutableArray.CreateBuilder<ValidatorModel>();

        foreach (ValidatorModel? item in items)
        {
            if (item is null) continue;
            if (item.IsAbstract || !item.HasPublicCtor) continue;
            validators.Add(item);
        }

        return new DiscoveredValidators(EquatableArray<ValidatorModel>.From(validators.ToImmutable()));
    }

    /// <summary>
    /// For testing: runs the discovery pipeline against an existing compilation. Must mirror
    /// Initialize()'s Stage 1 predicate and Stage 2 transform — keep in sync if those change.
    /// </summary>
    internal static DiscoveredValidators ExtractFromCompilation(
        Compilation compilation,
        CancellationToken ct = default)
    {
        ImmutableArray<ValidatorModel?>.Builder items = ImmutableArray.CreateBuilder<ValidatorModel?>();

        foreach (SyntaxTree syntaxTree in compilation.SyntaxTrees)
        {
            SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree);
            SyntaxNode root = syntaxTree.GetRoot(ct);

            foreach (SyntaxNode node in root.DescendantNodes())
            {
                if (!IsTypeDeclWithBaseList(node)) continue;
                if (node is not TypeDeclarationSyntax typeDecl) continue;
                if (semanticModel.GetDeclaredSymbol(typeDecl, ct) is not INamedTypeSymbol typeSymbol) continue;

                items.Add(ExtractFromSymbol(typeSymbol, ct));
            }
        }

        return BuildDiscoveredValidators(items.ToImmutable());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the fully-qualified metadata name for a named type symbol's original definition, in the
    /// form "Namespace.TypeName`arity" (matching <see cref="WellKnownTypeNames"/> constants).
    /// </summary>
    private static string GetMetadataFqn(INamedTypeSymbol symbol)
    {
        var parts = new List<string>();
        for (INamedTypeSymbol? current = symbol; current is not null; current = current.ContainingType)
            parts.Add(current.MetadataName);
        parts.Reverse();

        string ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString();

        string nameChain = string.Join("+", parts);
        return ns.Length == 0 ? nameChain : ns + "." + nameChain;
    }

    /// <summary>Returns the fully-qualified display name without the <c>global::</c> prefix.</summary>
    private static string GetFqn(ITypeSymbol symbol)
        => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                 .Replace("global::", string.Empty);

    private static bool HasPublicConstructor(INamedTypeSymbol symbol)
    {
        foreach (IMethodSymbol ctor in symbol.InstanceConstructors)
        {
            if (ctor.DeclaredAccessibility == Accessibility.Public)
                return true;
        }
        return false;
    }
}
