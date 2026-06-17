using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SkathIO.Rogue.Migration.Analyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TaskReturnTypeAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(MigrationDiagnostics.TaskReturnType);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;

        // Only analyze Handle methods.
        if (method.Name != "Handle") return;

        // Check if return type is Task or Task<T>.
        if (!IsTaskReturnType(method.ReturnType)) return;

        // Check if the containing type implements IRequestHandler or INotificationHandler.
        if (!ImplementsHandlerInterface(method.ContainingType)) return;

        foreach (var location in method.Locations)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MigrationDiagnostics.TaskReturnType,
                location,
                method.ContainingType.Name));
        }
    }

    private static bool IsTaskReturnType(ITypeSymbol type)
    {
        var name = type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return name == "global::System.Threading.Tasks.Task" ||
               name == "global::System.Threading.Tasks.Task<TResult>";
    }

    private static bool ImplementsHandlerInterface(INamedTypeSymbol type)
    {
        // Symbol path: fires on the MediatR-shaped handler interfaces while they still resolve (before
        // ROGM006 rewrites the base list).
        foreach (var iface in type.AllInterfaces)
        {
            var ifaceName = iface.OriginalDefinition.Name;
            if (ifaceName == "IRequestHandler" || ifaceName == "INotificationHandler")
                return true;
        }

        // Syntax path: after ROGM006 rewrites the base list to the CQS handler contracts
        // (ICommandHandler/IQueryHandler/IEventHandler/IStreamQueryHandler), those interfaces do not
        // resolve as symbols in the migration gate's stub compilation (only mscorlib + Tasks are
        // referenced), so AllInterfaces no longer includes them. Recognise them by base-list syntax so
        // ROGM002 keeps firing on a still-Task-returning Handle method until its return type is migrated.
        // The fixed-point loop applies ROGM006 (higher id) before ROGM002, so this is the live path for
        // most handlers.
        return ImplementsCqsHandlerInBaseListSyntax(type);
    }

    private static bool ImplementsCqsHandlerInBaseListSyntax(INamedTypeSymbol type)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not TypeDeclarationSyntax decl || decl.BaseList is null)
                continue;

            foreach (var baseType in decl.BaseList.Types)
            {
                var name = GetSimpleBaseName(baseType.Type);
                if (name is "ICommandHandler" or "IQueryHandler" or "IEventHandler" or "IStreamQueryHandler")
                    return true;
            }
        }

        return false;
    }

    private static string? GetSimpleBaseName(TypeSyntax type)
    {
        switch (type)
        {
            case IdentifierNameSyntax id:
                return id.Identifier.Text;
            case GenericNameSyntax g:
                return g.Identifier.Text;
            case QualifiedNameSyntax q:
                return GetSimpleBaseName(q.Right);
            default:
                return null;
        }
    }
}
