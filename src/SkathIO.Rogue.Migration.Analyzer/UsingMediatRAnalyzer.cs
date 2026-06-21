using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SkathIO.Rogue.Migration.Analyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UsingMediatRAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(MigrationDiagnostics.UsingMediatR);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeUsing, SyntaxKind.UsingDirective);
    }

    private static void AnalyzeUsing(SyntaxNodeAnalysisContext context)
    {
        var node = (UsingDirectiveSyntax)context.Node;
        if (node.Name?.ToString() == "MediatR")
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MigrationDiagnostics.UsingMediatR,
                node.GetLocation()));
        }
    }
}
