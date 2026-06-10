using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkathIO.Rogue.Migration.Analyzer;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ReplaceUsingMediatRCodeFix))]
[Shared]
public sealed class ReplaceUsingMediatRCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(MigrationDiagnostics.UsingMediatR.Id);

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var diagnostic = context.Diagnostics[0];
        var node = root.FindNode(diagnostic.Location.SourceSpan) as UsingDirectiveSyntax;
        if (node is null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Replace with SkathIO.Rogue using",
                createChangedDocument: ct => ReplaceUsing(context.Document, node, ct),
                equivalenceKey: "ReplaceUsingMediatR"),
            diagnostic);
    }

    private static async Task<Document> ReplaceUsing(
        Document document,
        UsingDirectiveSyntax usingNode,
        CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null) return document;

        // Emit only `using SkathIO.Rogue;` — the marker interfaces (IRequest<>, IRequestHandler<,>,
        // INotification, ISender, …) all live there, so migrated declarations resolve unambiguously.
        // Adding `using SkathIO.Rogue.Compatibility;` as well would make every such reference a CS0104
        // ambiguous reference (the compat shim re-declares the same names); the compat namespace is an
        // opt-in transitional aid for DI-only call sites (AddMediatR/Unit.Value/ReflectionMediator),
        // which migrators add by hand. See PD-32a.
        var rogueUsing = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("SkathIO.Rogue"))
            .WithLeadingTrivia(usingNode.GetLeadingTrivia())
            .WithTrailingTrivia(usingNode.GetTrailingTrivia());

        var newRoot = root.ReplaceNode(usingNode, rogueUsing);
        return document.WithSyntaxRoot(newRoot);
    }
}
