using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SkathIO.Rogue.Migration.Analyzer;

/// <summary>
/// Reports each type declaration whose base list references a MediatR marker/handler interface
/// (<c>IRequest</c>/<c>IRequestHandler</c>/<c>INotification</c>/<c>INotificationHandler</c>/
/// <c>IStreamRequest</c>/<c>IStreamRequestHandler</c>), so <see cref="MigrateMediatRMarkerTypeCodeFix"/>
/// can rewrite it onto the post-D5 SkathIO.Rogue CQS contract (ROGM006). When a response-bearing
/// request's command-vs-query intent cannot be inferred from its name it additionally emits ROGM005
/// (manual-review) — it is still migrated to the safe-default <c>ICommand&lt;T&gt;</c>, never silently
/// mis-mapped as a query (F8 E1, PD-44 amendment).
/// </summary>
/// <remarks>
/// Syntax-driven (base-list scan) rather than symbol-driven: the AC-F migration gate compiles the
/// sample against in-source MediatR stubs, and a base-list name scan fires identically whether or not
/// the MediatR symbols resolve, which keeps the analyzer robust across the fixed-point apply loop.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MediatRMarkerTypeAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            MigrationDiagnostics.MediatRMarkerType,
            MigrationDiagnostics.AmbiguousCommandOrQuery);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeTypeDeclaration,
            SyntaxKind.ClassDeclaration,
            SyntaxKind.RecordDeclaration,
            SyntaxKind.StructDeclaration,
            SyntaxKind.RecordStructDeclaration);
    }

    private static void AnalyzeTypeDeclaration(SyntaxNodeAnalysisContext context)
    {
        var type = (TypeDeclarationSyntax)context.Node;
        if (type.BaseList is null) return;

        foreach (var baseType in type.BaseList.Types)
        {
            var simpleName = GetSimpleName(baseType.Type);
            if (simpleName is null || !MediatRTypeMapping.IsMediatRInterfaceName(simpleName))
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                MigrationDiagnostics.MediatRMarkerType,
                baseType.Type.GetLocation(),
                type.Identifier.Text,
                simpleName));

            // ROGM005: a response-bearing IRequest<T> whose intent the name cannot disambiguate is
            // migrated to the safe-default ICommand<T> and flagged for manual review (never silently a
            // query). Only the request type itself carries the intent signal — read it from the request
            // declaration, not the handler. A no-response `IRequest` (no type argument) is always a void
            // command; it carries no ambiguity.
            if (simpleName == "IRequest"
                && baseType.Type is GenericNameSyntax genericRequest
                && genericRequest.TypeArgumentList.Arguments.Count == 1
                && MediatRTypeMapping.ClassifyByName(type.Identifier.Text)
                    == MediatRTypeMapping.RequestIntent.Ambiguous)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    MigrationDiagnostics.AmbiguousCommandOrQuery,
                    type.Identifier.GetLocation(),
                    type.Identifier.Text));
            }
        }
    }

    private static string? GetSimpleName(TypeSyntax type)
    {
        switch (type)
        {
            case IdentifierNameSyntax id:
                return id.Identifier.Text;
            case GenericNameSyntax g:
                return g.Identifier.Text;
            case QualifiedNameSyntax q:
                return GetSimpleName(q.Right);
            default:
                return null;
        }
    }
}
