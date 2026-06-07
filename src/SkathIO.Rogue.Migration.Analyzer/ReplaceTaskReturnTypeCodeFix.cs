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

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ReplaceTaskReturnTypeCodeFix))]
[Shared]
public sealed class ReplaceTaskReturnTypeCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(MigrationDiagnostics.TaskReturnType.Id);

    public override FixAllProvider GetFixAllProvider() =>
        WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        var diagnostic = context.Diagnostics[0];
        // Find the method declaration that contains the diagnostic location.
        var node = root.FindNode(diagnostic.Location.SourceSpan);
        var method = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (method is null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Replace Task return type with ValueTask",
                createChangedDocument: ct => ReplaceTaskReturn(context.Document, method, ct),
                equivalenceKey: "ReplaceTaskReturnType"),
            diagnostic);
    }

    private static async Task<Document> ReplaceTaskReturn(
        Document document,
        MethodDeclarationSyntax method,
        CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null) return document;

        var returnType = method.ReturnType;
        TypeSyntax newReturnType;

        if (returnType is GenericNameSyntax generic && generic.Identifier.Text == "Task")
        {
            // Plain Task<T> → ValueTask<T>
            newReturnType = SyntaxFactory.GenericName(
                    SyntaxFactory.Identifier("ValueTask"),
                    generic.TypeArgumentList)
                .WithTriviaFrom(returnType);
        }
        else if (returnType is QualifiedNameSyntax qualifiedGenericName
                 && qualifiedGenericName.Right is GenericNameSyntax qualifiedGeneric
                 && qualifiedGeneric.Identifier.Text == "Task")
        {
            // Qualified Task<T> (e.g. System.Threading.Tasks.Task<int>) → ValueTask<T>
            newReturnType = SyntaxFactory.GenericName(
                    SyntaxFactory.Identifier("ValueTask"),
                    qualifiedGeneric.TypeArgumentList)
                .WithTriviaFrom(returnType);
        }
        else if (returnType is QualifiedNameSyntax qualifiedSimpleName
                 && qualifiedSimpleName.Right is IdentifierNameSyntax qualifiedRight
                 && qualifiedRight.Identifier.Text == "Task")
        {
            // Qualified Task (e.g. System.Threading.Tasks.Task) → ValueTask
            newReturnType = SyntaxFactory.IdentifierName("ValueTask")
                .WithTriviaFrom(returnType);
        }
        else
        {
            // Plain Task → ValueTask
            newReturnType = SyntaxFactory.IdentifierName("ValueTask")
                .WithTriviaFrom(returnType);
        }

        var newMethod = method.WithReturnType(newReturnType);
        var newRoot = root.ReplaceNode(method, newMethod);
        return document.WithSyntaxRoot(newRoot);
    }
}
