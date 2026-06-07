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
                title: "Replace with SkathIO.Rogue usings",
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

        // Build replacement usings.
        var rogueUsing = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("SkathIO.Rogue"))
            .WithLeadingTrivia(usingNode.GetLeadingTrivia())
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

        var compatUsing = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("SkathIO.Rogue.Compatibility"))
            .WithTrailingTrivia(usingNode.GetTrailingTrivia());

        // Replace the single MediatR using with two usings.
        var newRoot = root.ReplaceNode(usingNode, new SyntaxNode[] { rogueUsing, compatUsing });
        return document.WithSyntaxRoot(newRoot);
    }
}
