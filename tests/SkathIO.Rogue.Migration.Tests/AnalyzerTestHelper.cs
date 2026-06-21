using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;

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

    /// <summary>
    /// Loads all <c>.cs</c> files under <paramref name="sampleDirectory"/> into one
    /// <see cref="AdhocWorkspace"/> project, runs every <paramref name="analyzers"/>, and applies
    /// every matching code-fix in <paramref name="fixers"/> in a fixed-point loop until no ROGM00x
    /// diagnostics remain. Returns the fixed source texts keyed by file name. The compilation
    /// references only mscorlib + System.Threading.Tasks; the sample's stub <c>MediatR</c> namespace
    /// lives in-source (MediatRStubs.cs), so no MediatR package is needed.
    /// </summary>
    /// <remarks>
    /// One diagnostic is fixed per loop pass (re-analyzing after each application), so the iteration
    /// cap must exceed the total fixable diagnostic count across the whole sample. The cap exists
    /// only as an infinite-loop guard (a code-fix that fails to clear its own diagnostic); the real
    /// time bound is asserted by the caller's wall-clock ceiling.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, string>> ApplyAllFixesAsync(
        string sampleDirectory,
        IReadOnlyList<DiagnosticAnalyzer> analyzers,
        IReadOnlyList<CodeFixProvider> fixers,
        CancellationToken cancellationToken = default)
    {
        var files = Directory.GetFiles(sampleDirectory, "*.cs", SearchOption.AllDirectories);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Sample", "Sample", LanguageNames.CSharp)
            .WithProjectCompilationOptions(
                projectId,
                new CSharpCompilationOptions(OutputKind.ConsoleApplication))
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(CancellationToken).Assembly.Location))
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(ValueTask).Assembly.Location));

        // Map document name -> id so the fixed-point loop can re-read each document's text.
        var docIds = new Dictionary<string, DocumentId>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var docId = DocumentId.CreateNewId(projectId);
            docIds[name] = docId;
            solution = solution.AddDocument(docId, name, File.ReadAllText(path));
        }

        var analyzerArray = ImmutableArray.CreateRange(analyzers);
        var emptyOptions = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty);

        // One fix per pass; cap generously above the sample's total fixable diagnostic count. This
        // is an infinite-loop guard only — convergence normally happens well before the cap.
        const int maxIterations = 500;
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var project = solution.GetProject(projectId)!;
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            var diagnostics = await compilation!
                .WithAnalyzers(analyzerArray, emptyOptions)
                .GetAnalyzerDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);

            // Only auto-fixable migration diagnostics drive the loop. ROGM003 (open-generic) is
            // Info-only with no code-fix, so it never blocks convergence.
            //
            // Ordering matters: ROGM002 (handler Task->ValueTask) is detected via the handler's
            // *interface implementation*, which is only resolvable while the MediatR using is still
            // present. ROGM001 (using rewrite) removes that using, so we must clear all ROGM002
            // diagnostics before applying ROGM001 — otherwise stripping the using first would make
            // the handler interfaces unresolvable and ROGM002 would silently stop firing. Sorting by
            // descending diagnostic id puts ROGM002 ahead of ROGM001.
            var fixable = diagnostics
                .Where(d => fixers.Any(f => f.FixableDiagnosticIds.Contains(d.Id)))
                .OrderByDescending(d => d.Id, StringComparer.Ordinal)
                .ToImmutableArray();
            if (fixable.IsEmpty)
            {
                break;
            }

            var diagnostic = fixable[0];
            var fixer = fixers.First(f => f.FixableDiagnosticIds.Contains(diagnostic.Id));
            var document = solution.GetDocument(diagnostic.Location.SourceTree)!;

            var actions = new List<CodeAction>();
            var fixContext = new CodeFixContext(
                document, diagnostic, (a, _) => actions.Add(a), cancellationToken);
            await fixer.RegisterCodeFixesAsync(fixContext).ConfigureAwait(false);
            if (actions.Count == 0)
            {
                break;
            }

            var operations = await actions[0].GetOperationsAsync(cancellationToken).ConfigureAwait(false);
            var applyOp = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
            if (applyOp is null)
            {
                break;
            }

            solution = applyOp.ChangedSolution;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, docId) in docIds)
        {
            var doc = solution.GetDocument(docId)!;
            result[name] = (await doc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
        }

        return result;
    }

    /// <summary>
    /// Recompiles <paramref name="fixedSources"/> (file name -> source) against
    /// <paramref name="references"/>, emits to a <see cref="MemoryStream"/>, and — if emission has no
    /// errors — loads the assembly and invokes its entry point. Returns whether the recompile produced
    /// zero errors, the collected error diagnostics, and the wall-clock elapsed milliseconds.
    /// </summary>
    public static (bool ZeroErrors, ImmutableArray<Diagnostic> Errors, long ElapsedMs)
        RecompileAndRun(
            IReadOnlyDictionary<string, string> fixedSources,
            IEnumerable<MetadataReference> references)
    {
        var stopwatch = Stopwatch.StartNew();

        var trees = fixedSources
            .Select(kv => CSharpSyntaxTree.ParseText(kv.Value, path: kv.Key))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "MigratedSample",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));

        using var stream = new MemoryStream();
        EmitResult emitResult = compilation.Emit(stream);

        var errors = emitResult.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        var zeroErrors = errors.IsEmpty;

        if (zeroErrors)
        {
            stream.Seek(0, SeekOrigin.Begin);
            var assembly = Assembly.Load(stream.ToArray());
            var entryPoint = assembly.EntryPoint
                ?? throw new InvalidOperationException("Migrated sample has no entry point.");
            InvokeEntryPoint(entryPoint);
        }

        stopwatch.Stop();
        return (zeroErrors, errors, stopwatch.ElapsedMilliseconds);
    }

    private static void InvokeEntryPoint(MethodInfo entryPoint)
    {
        // Top-level-statement entry points are generated as `Main(string[])` (sync) or
        // `<Main>$`/`Main` returning Task for top-level `await`. Handle both shapes.
        var args = entryPoint.GetParameters().Length == 1 ? new object?[] { Array.Empty<string>() } : null;
        var result = entryPoint.Invoke(null, args);
        if (result is Task task)
        {
            task.GetAwaiter().GetResult();
        }
    }
}
