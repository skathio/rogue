using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkathIO.Rogue.SourceGenerator;

/// <summary>
/// Reads the validation feature module's model from the compilation: the <c>[Validate]</c> types with
/// their rule attributes, nested and collection properties and <c>Rules</c> hook, the
/// <c>IAsyncRules&lt;T&gt;</c> classes, and the misuse the build must report. No symbol survives past
/// these methods, so the incremental cache compares plain records.
/// </summary>
internal static class ValidationDiscovery
{
    internal const string ValidatorMethodName = "__RogueValidate";
    internal const string RulesHookName = "Rules";

    private static readonly SymbolDisplayFormat _typeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    internal static ValidatedTypeModel? ExtractValidatedType(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
        {
            return null;
        }

        Compilation compilation = ctx.SemanticModel.Compilation;
        var diagnostics = ImmutableArray.CreateBuilder<ValidationDiagnosticModel>();
        bool canEmit = IsPartialChain(type);
        if (!canEmit)
        {
            diagnostics.Add(Diagnostic("ROGUE014", type.ToDisplayString()));
        }

        var reader = new PropertyReader(compilation, diagnostics);
        var properties = ImmutableArray.CreateBuilder<ValidatedPropertyModel>();
        foreach (IPropertySymbol property in ValidatedProperties(type))
        {
            ct.ThrowIfCancellationRequested();
            if (reader.Read(type, property) is { } model)
            {
                properties.Add(model);
            }
        }

        return new ValidatedTypeModel(
            TypeFqn: type.ToDisplayString(_typeFormat),
            Namespace: type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            Declarations: Declarations(type),
            CanEmit: canEmit,
            IsReferenceType: type.IsReferenceType,
            HasRulesHook: ReadRulesHook(type, compilation, diagnostics),
            Properties: EquatableArray<ValidatedPropertyModel>.From(properties.ToImmutable()),
            Diagnostics: EquatableArray<ValidationDiagnosticModel>.From(diagnostics.ToImmutable()));
    }

    internal static AsyncRulesModel? ExtractAsyncRules(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol type)
        {
            return null;
        }

        foreach (INamedTypeSymbol iface in type.AllInterfaces)
        {
            if (iface.TypeArguments.Length == 1 && MetadataName(iface.OriginalDefinition) == WellKnownTypeNames.IAsyncRules1)
            {
                ITypeSymbol request = iface.TypeArguments[0];
                bool canRegister = !type.IsAbstract && !type.IsGenericType && HasPublicConstructor(type);
                return new AsyncRulesModel(
                    type.ToDisplayString(_typeFormat),
                    request.ToDisplayString(_typeFormat),
                    canRegister,
                    HasValidateAttribute(request));
            }
        }
        return null;
    }

    /// <summary>
    /// For a property declaration that carries a rule attribute on a type without <c>[Validate]</c>,
    /// returns the type's display name (ROGUE018); otherwise null.
    /// </summary>
    internal static string? ExtractUnmarkedRuleOwner(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not IPropertySymbol property
            || property.ContainingType is not { } owner
            || HasValidateAttribute(owner))
        {
            return null;
        }

        INamedTypeSymbol? ruleBase = ctx.SemanticModel.Compilation.GetTypeByMetadataName(WellKnownTypeNames.RuleAttribute);
        foreach (AttributeData attr in property.GetAttributes())
        {
            if (DerivesFrom(attr.AttributeClass, ruleBase))
            {
                return owner.ToDisplayString();
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the [Validate] request types, among those handled in this compilation, that come from a
    /// referenced assembly built without the generator: they carry no validator to call (ROGUE017).
    /// </summary>
    internal static EquatableArray<string> FindRequestsWithoutValidator(DiscoveredModels models, Compilation compilation, CancellationToken ct)
    {
        var missing = ImmutableArray.CreateBuilder<string>();
        foreach (HandlerModel handler in models.Handlers)
        {
            ct.ThrowIfCancellationRequested();
            if (!FeatureModules.Marks(handler, FeatureModules.ValidationModule))
            {
                continue;
            }

            // Nested request types have no reliable metadata name here; they are validated as usual and,
            // if the validator is missing, the generated call fails the build instead.
            INamedTypeSymbol? request = compilation.GetTypeByMetadataName(handler.RequestFqn);
            if (request is { Locations.Length: > 0 } && request.Locations[0].IsInMetadata && !HasGeneratedValidator(request))
            {
                missing.Add(handler.RequestFqn);
            }
        }
        return EquatableArray<string>.From(missing.ToImmutable());
    }

    internal static bool IsValidationRuntimeAvailable(Compilation compilation)
        => compilation.GetTypeByMetadataName(WellKnownTypeNames.ValidationErrors) is not null;

    internal static bool HasValidateAttribute(ITypeSymbol type)
    {
        foreach (AttributeData attr in type.GetAttributes())
        {
            if (attr.AttributeClass is { } attrClass && MetadataName(attrClass) == WellKnownTypeNames.ValidateAttribute)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a validator can be called on <paramref name="type"/>: a source type gets one from this run
    /// when it can be emitted, and a referenced type must already carry it.
    /// </summary>
    internal static bool HasCallableValidator(INamedTypeSymbol type)
        => type.Locations.Length > 0 && type.Locations[0].IsInSource ? IsPartialChain(type) : HasGeneratedValidator(type);

    internal static bool HasGeneratedValidator(INamedTypeSymbol type) => type.GetMembers(ValidatorMethodName).Length > 0;

    internal static bool DerivesFrom(INamedTypeSymbol? type, INamedTypeSymbol? baseType)
    {
        for (INamedTypeSymbol? current = type?.BaseType; current is not null && baseType is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }
        return false;
    }

    internal static ValidationDiagnosticModel Diagnostic(string id, params string[] args)
        => new ValidationDiagnosticModel(id, EquatableArray<string>.From(ImmutableArray.Create(args)));

    private static bool IsPartialChain(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            foreach (SyntaxReference reference in current.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is not TypeDeclarationSyntax declaration
                    || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    return false;
                }
            }
        }
        return true;
    }

    // Public instance properties with a getter, the type's own first and then inherited ones, each name
    // once (an override or a hiding member wins over the base member).
    private static IEnumerable<IPropertySymbol> ValidatedProperties(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>();
        for (INamedTypeSymbol? current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers())
            {
                if (member is IPropertySymbol { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public, GetMethod: not null } property
                    && !property.IsImplicitlyDeclared
                    && seen.Add(property.Name))
                {
                    yield return property;
                }
            }
        }
    }

    private static bool ReadRulesHook(INamedTypeSymbol type, Compilation compilation, ImmutableArray<ValidationDiagnosticModel>.Builder diagnostics)
    {
        INamedTypeSymbol? errorsType = compilation.GetTypeByMetadataName(WellKnownTypeNames.ValidationErrors);
        bool found = false;
        foreach (ISymbol member in type.GetMembers(RulesHookName))
        {
            if (member is not IMethodSymbol method)
            {
                continue;
            }
            found = true;
            if (method is { IsStatic: true, ReturnsVoid: true, IsGenericMethod: false, Parameters.Length: 2 }
                && method.Parameters[0].RefKind == RefKind.None
                && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, type)
                && method.Parameters[1].RefKind == RefKind.Ref
                && SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type, errorsType))
            {
                return true;
            }
        }

        if (found)
        {
            diagnostics.Add(Diagnostic("ROGUE016", type.ToDisplayString(), type.Name));
        }
        return false;
    }

    private static EquatableArray<TypeDeclarationModel> Declarations(INamedTypeSymbol type)
    {
        var chain = new List<TypeDeclarationModel>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            chain.Add(new TypeDeclarationModel(Keyword(current), NameWithTypeParameters(current)));
        }
        chain.Reverse();
        return EquatableArray<TypeDeclarationModel>.From(chain.ToImmutableArray());
    }

    private static string Keyword(INamedTypeSymbol type)
    {
        string kind = type.TypeKind switch
        {
            TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
            TypeKind.Interface => "interface",
            _ => type.IsRecord ? "record" : "class",
        };
        return type.TypeKind == TypeKind.Struct && type.IsReadOnly ? "readonly " + kind : kind;
    }

    private static string NameWithTypeParameters(INamedTypeSymbol type)
    {
        if (type.TypeParameters.Length == 0)
        {
            return type.Name;
        }

        var names = new List<string>();
        foreach (ITypeParameterSymbol parameter in type.TypeParameters)
        {
            names.Add(parameter.Name);
        }
        return type.Name + "<" + string.Join(", ", names) + ">";
    }

    private static bool HasPublicConstructor(INamedTypeSymbol type)
    {
        foreach (IMethodSymbol ctor in type.InstanceConstructors)
        {
            if (ctor.DeclaredAccessibility == Accessibility.Public)
            {
                return true;
            }
        }
        return false;
    }

    internal static string MetadataName(INamedTypeSymbol type)
    {
        string name = type.MetadataName;
        for (INamedTypeSymbol? outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            name = outer.MetadataName + "+" + name;
        }
        return type.ContainingNamespace.IsGlobalNamespace ? name : type.ContainingNamespace.ToDisplayString() + "." + name;
    }
}
