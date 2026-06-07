using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
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
        foreach (var iface in type.AllInterfaces)
        {
            var ifaceName = iface.OriginalDefinition.Name;
            if (ifaceName == "IRequestHandler" || ifaceName == "INotificationHandler")
                return true;
        }
        return false;
    }
}
