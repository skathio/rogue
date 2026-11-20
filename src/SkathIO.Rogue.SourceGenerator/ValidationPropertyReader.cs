using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SkathIO.Rogue.SourceGenerator;

/// <summary>
/// Reads what the generated validator checks on one property: its rule attributes, whether null is an
/// error, and whether it holds a nested <c>[Validate]</c> object or a collection of them. Misuse is added
/// to the diagnostics list given at construction.
/// </summary>
internal sealed class PropertyReader
{
    private const string NotFitting = "ROGUE015";

    private readonly Compilation _compilation;
    private readonly ImmutableArray<ValidationDiagnosticModel>.Builder _diagnostics;
    private readonly INamedTypeSymbol? _ruleBase;

    internal PropertyReader(Compilation compilation, ImmutableArray<ValidationDiagnosticModel>.Builder diagnostics)
    {
        _compilation = compilation;
        _diagnostics = diagnostics;
        _ruleBase = compilation.GetTypeByMetadataName(WellKnownTypeNames.RuleAttribute);
    }

    internal ValidatedPropertyModel? Read(INamedTypeSymbol owner, IPropertySymbol property)
    {
        ITypeSymbol type = property.Type;
        EquatableArray<RuleModel> rules = ReadRules(owner, property);
        bool impliedNotNull = type.IsReferenceType && property.NullableAnnotation == NullableAnnotation.NotAnnotated;

        NestedKind nested = NestedKind.None;
        ITypeSymbol? nestedType = null;
        ITypeSymbol? itemType = null;
        CollectionShape shape = CollectionShape.Foreach;
        if (CallableValidatedType(owner, property, type) is { } objectType)
        {
            nested = NestedKind.Object;
            nestedType = objectType;
        }
        else if (CollectionItem(type, out shape) is { } item && CallableValidatedType(owner, property, item) is { } validatedItem)
        {
            nested = NestedKind.Collection;
            nestedType = validatedItem;
            itemType = item;
        }

        if (rules.Count == 0 && !impliedNotNull && nested == NestedKind.None)
        {
            return null;
        }

        return new ValidatedPropertyModel(
            Name: property.Name,
            Identifier: SyntaxFacts.GetKeywordKind(property.Name) == SyntaxKind.None ? property.Name : "@" + property.Name,
            CanBeNull: CanBeNull(type),
            ImpliedNotNull: impliedNotNull,
            Rules: rules,
            Nested: nested,
            NestedTypeFqn: nestedType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Shape: shape,
            ItemCanBeNull: itemType is not null && CanBeNull(itemType),
            ItemImpliedNotNull: itemType is { IsReferenceType: true, NullableAnnotation: NullableAnnotation.NotAnnotated });
    }

    private static bool CanBeNull(ITypeSymbol type)
        => type.IsReferenceType || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    // The [Validate] type behind a property or collection item (looking through Nullable<T>), when its
    // validator can be called. A referenced [Validate] type without a generated validator is reported
    // rather than skipped, so a nested part is never left unchecked without a word.
    private ITypeSymbol? CallableValidatedType(INamedTypeSymbol owner, IPropertySymbol property, ITypeSymbol type)
    {
        ITypeSymbol candidate = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;
        if (candidate is not INamedTypeSymbol named || !ValidationDiscovery.HasValidateAttribute(named))
        {
            return null;
        }

        if (ValidationDiscovery.HasCallableValidator(named))
        {
            return named;
        }

        if (named.Locations.Length > 0 && named.Locations[0].IsInMetadata)
        {
            _diagnostics.Add(ValidationDiscovery.Diagnostic("ROGUE017", named.ToDisplayString(), owner.ToDisplayString() + "." + property.Name));
        }
        return null;
    }

    // The item type of an array or IEnumerable<T> property (strings excluded), with the loop shape that
    // walks it without allocating where the declared type allows.
    private static ITypeSymbol? CollectionItem(ITypeSymbol type, out CollectionShape shape)
    {
        shape = CollectionShape.Foreach;
        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            return array.ElementType;
        }
        if (type.SpecialType == SpecialType.System_String || type is not INamedTypeSymbol named)
        {
            return null;
        }

        INamedTypeSymbol? enumerable = FindInterface(named, SpecialType.System_Collections_Generic_IEnumerable_T);
        if (enumerable is null)
        {
            return null;
        }

        if (named.TypeKind == TypeKind.Interface)
        {
            shape = FindInterface(named, SpecialType.System_Collections_Generic_IReadOnlyList_T) is not null ? CollectionShape.ReadOnlyList
                : FindInterface(named, SpecialType.System_Collections_Generic_IList_T) is not null ? CollectionShape.List
                : CollectionShape.Foreach;
        }
        return enumerable.TypeArguments[0];
    }

    private static INamedTypeSymbol? FindInterface(INamedTypeSymbol type, SpecialType special)
    {
        if (type.OriginalDefinition.SpecialType == special)
        {
            return type;
        }
        foreach (INamedTypeSymbol iface in type.AllInterfaces)
        {
            if (iface.OriginalDefinition.SpecialType == special)
            {
                return iface;
            }
        }
        return null;
    }

    private EquatableArray<RuleModel> ReadRules(INamedTypeSymbol owner, IPropertySymbol property)
    {
        var rules = ImmutableArray.CreateBuilder<RuleModel>();
        foreach (AttributeData attr in property.GetAttributes())
        {
            if (attr.AttributeClass is not { } rule || !ValidationDiscovery.DerivesFrom(rule, _ruleBase))
            {
                continue;
            }

            if (RuleContract.Problem(rule, attr.ConstructorArguments.Length, property.Type, _compilation) is { } problem)
            {
                _diagnostics.Add(ValidationDiscovery.Diagnostic(NotFitting, rule.Name, owner.ToDisplayString() + "." + property.Name, problem));
                continue;
            }
            rules.Add(ToModel(rule, attr));
        }
        return EquatableArray<RuleModel>.From(rules.ToImmutable());
    }

    private static RuleModel ToModel(INamedTypeSymbol rule, AttributeData attr)
    {
        var arguments = ImmutableArray.CreateBuilder<string>();
        foreach (TypedConstant argument in attr.ConstructorArguments)
        {
            arguments.Add(argument.ToCSharpString());
        }

        string? code = null;
        string? message = null;
        foreach (var named in attr.NamedArguments)
        {
            if (named.Value.Value is string text && named.Key is "Code" or "Message")
            {
                string literal = SymbolDisplay.FormatLiteral(text, quote: true);
                code = named.Key == "Code" ? literal : code;
                message = named.Key == "Message" ? literal : message;
            }
        }

        return new RuleModel(
            rule.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            EquatableArray<string>.From(arguments.ToImmutable()),
            code,
            message);
    }
}
