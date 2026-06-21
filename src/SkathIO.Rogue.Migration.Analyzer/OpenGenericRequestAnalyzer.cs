using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SkathIO.Rogue.Migration.Analyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OpenGenericRequestAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(MigrationDiagnostics.OpenGenericRequest);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        // Only interested in generic types (open-generic requests).
        if (!type.IsGenericType) return;

        // Check if it implements IRequest<> / IRequest / IStreamRequest.
        bool implementsRequest = false;
        foreach (var iface in type.AllInterfaces)
        {
            var name = iface.OriginalDefinition.Name;
            if (name == "IRequest" || name == "IStreamRequest")
            {
                implementsRequest = true;
                break;
            }
        }

        if (!implementsRequest) return;

        foreach (var location in type.Locations)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MigrationDiagnostics.OpenGenericRequest,
                location,
                type.Name));
        }
    }
}
