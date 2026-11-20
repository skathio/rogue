using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace SkathIO.Rogue.Jobs.CodeFixes;

/// <summary>
/// Fixes ROGUE019 by adding <c>[JsonSerializable(typeof(T))]</c> for the uncovered job type to the
/// project's <c>JsonSerializerContext</c>. It lives apart from the analyzer so the analyzer does not
/// load the Workspaces layer.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(JobSerializerCoverageCodeFix))]
[Shared]
public sealed class JobSerializerCoverageCodeFix : CodeFixProvider
{
    // Kept in step with the analyzer's descriptor and property name; the two assemblies do not reference each other.
    private const string DiagnosticId = "ROGUE019";
    private const string JobTypeProperty = "JobType";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticId);

    /// <inheritdoc />
    /// <remarks>
    /// No fix-all: every fix edits the same context declaration, and the batch fixer drops edits that
    /// overlap, so a fix-all would silently add only some of the attributes.
    /// </remarks>
    public override FixAllProvider? GetFixAllProvider() => null;

    /// <inheritdoc />
    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            if (diagnostic.AdditionalLocations.Count == 0
                || !diagnostic.Properties.TryGetValue(JobTypeProperty, out string? jobType)
                || jobType is null)
            {
                continue;
            }

            Location target = diagnostic.AdditionalLocations[0];
            Document? contextDocument = context.Document.Project.GetDocument(target.SourceTree);
            if (contextDocument is null)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Add [JsonSerializable(typeof(" + jobType.Replace("global::", string.Empty) + "))]",
                    createChangedSolution: ct => AddJsonSerializableAsync(contextDocument, target, jobType, ct),
                    equivalenceKey: DiagnosticId + ":" + jobType),
                diagnostic);
        }
        return Task.CompletedTask;
    }

    private static async Task<Solution> AddJsonSerializableAsync(Document document, Location target, string jobType, CancellationToken cancellationToken)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        ClassDeclarationSyntax? declaration = root?.FindNode(target.SourceSpan).AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (root is null || declaration is null)
        {
            return document.Project.Solution;
        }

        AttributeListSyntax attribute = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(
                    SyntaxFactory.ParseName("global::System.Text.Json.Serialization.JsonSerializable"),
                    SyntaxFactory.ParseAttributeArgumentList("(typeof(" + jobType + "))"))))
            .WithAdditionalAnnotations(Simplifier.Annotation, Formatter.Annotation);

        SyntaxNode updated = root.ReplaceNode(declaration, declaration.AddAttributeLists(attribute));
        return document.Project.Solution.WithDocumentSyntaxRoot(document.Id, updated);
    }
}
