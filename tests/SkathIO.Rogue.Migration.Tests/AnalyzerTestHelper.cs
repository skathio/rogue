using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SkathIO.Rogue.Migration.Tests;

internal static class AnalyzerTestHelper
{
    public static async Task<ImmutableArray<Diagnostic>> RunAsync(
        string sourceCode,
        DiagnosticAnalyzer analyzer)
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { CSharpSyntaxTree.ParseText(sourceCode) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compilationWithAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create(analyzer),
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty));

        return await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None);
    }

    /// <summary>
    /// Runs <paramref name="analyzer"/> over <paramref name="sourceCode"/>, applies the first
    /// code action registered by <paramref name="fixer"/> for the first analyzer diagnostic, and
    /// returns the resulting document text. Uses an <see cref="AdhocWorkspace"/> so no Roslyn
    /// testing package is required.
    /// </summary>
    public static async Task<string> ApplyCodeFixAsync(
        string sourceCode,
        DiagnosticAnalyzer analyzer,
        CodeFixProvider fixer)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp)
            .WithProjectCompilationOptions(
                projectId,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(
                projectId,
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument(docId, "Test.cs", sourceCode);

        var document = solution.GetDocument(docId)!;
        var compilation = await document.Project.GetCompilationAsync(CancellationToken.None);

        var compilationWithAnalyzers = compilation!.WithAnalyzers(
            ImmutableArray.Create(analyzer),
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty));
        var diagnostics =
            await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None);

        if (diagnostics.IsEmpty)
        {
            return sourceCode;
        }

        var diagnostic = diagnostics[0];
        var actions = new List<CodeAction>();
        var fixContext = new CodeFixContext(
            document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None);
        await fixer.RegisterCodeFixesAsync(fixContext);

        if (actions.Count == 0)
        {
            return sourceCode;
        }

        var operations = await actions[0].GetOperationsAsync(CancellationToken.None);
        var applyOp = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
        if (applyOp is null)
        {
            return sourceCode;
        }

        var newDoc = applyOp.ChangedSolution.GetDocument(docId)!;
        return (await newDoc.GetTextAsync(CancellationToken.None)).ToString();
    }
}
