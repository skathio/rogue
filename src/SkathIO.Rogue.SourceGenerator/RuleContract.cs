using Microsoft.CodeAnalysis;

namespace SkathIO.Rogue.SourceGenerator;

/// <summary>
/// Checks that a rule attribute fits the property it is placed on. A rule supplies static
/// <c>IsValid(value, args...)</c> overloads, a <c>DefaultCode</c> constant and a
/// <c>DefaultMessage(propertyName, args...)</c> method; the generated validator calls them directly. A
/// rule that does not fit would otherwise surface as a compile error inside generated code, so it is
/// reported against the property instead (ROGUE015).
/// </summary>
internal static class RuleContract
{
    /// <summary>Returns why the rule cannot check the property, or null when it can.</summary>
    internal static string? Problem(INamedTypeSymbol rule, int argumentCount, ITypeSymbol propertyType, Compilation compilation)
    {
        if (!HasDefaultCode(rule) || !HasStaticMethod(rule, "DefaultMessage", argumentCount + 1))
        {
            return "the rule has no public 'DefaultCode' constant or static 'DefaultMessage' method taking the property name and "
                + argumentCount + " argument(s)";
        }

        for (INamedTypeSymbol? current = rule; current is not null; current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers("IsValid"))
            {
                if (member is IMethodSymbol method && IsCheck(method, argumentCount) && Accepts(method, propertyType, compilation))
                {
                    return null;
                }
            }
        }
        return "no public static 'IsValid' method takes a value of type '" + propertyType.ToDisplayString() + "' and "
            + argumentCount + " argument(s)";
    }

    private static bool IsCheck(IMethodSymbol method, int argumentCount)
        => method is { IsStatic: true, DeclaredAccessibility: Accessibility.Public }
            && method.ReturnType.SpecialType == SpecialType.System_Boolean
            && method.Parameters.Length == argumentCount + 1;

    private static bool HasDefaultCode(INamedTypeSymbol rule)
    {
        for (INamedTypeSymbol? current = rule; current is not null; current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers("DefaultCode"))
            {
                if (member is IFieldSymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public, Type.SpecialType: SpecialType.System_String })
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasStaticMethod(INamedTypeSymbol rule, string name, int parameterCount)
    {
        for (INamedTypeSymbol? current = rule; current is not null; current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers(name))
            {
                if (member is IMethodSymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public } method
                    && method.Parameters.Length == parameterCount)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // Whether the value parameter accepts the property type: by implicit conversion for a plain
    // parameter, and for a generic one by the property type being, implementing or deriving from the
    // parameter's generic type (type inference then binds the type argument).
    private static bool Accepts(IMethodSymbol method, ITypeSymbol propertyType, Compilation compilation)
    {
        ITypeSymbol parameter = method.Parameters[0].Type;
        if (!method.IsGenericMethod)
        {
            return compilation.ClassifyCommonConversion(propertyType, parameter).IsImplicit;
        }
        if (parameter is ITypeParameterSymbol)
        {
            return true;
        }
        return parameter is INamedTypeSymbol { IsGenericType: true } generic && HasGenericShape(propertyType, generic.OriginalDefinition);
    }

    private static bool HasGenericShape(ITypeSymbol type, INamedTypeSymbol definition)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, definition))
            {
                return true;
            }
        }
        foreach (INamedTypeSymbol iface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, definition))
            {
                return true;
            }
        }
        return false;
    }
}
