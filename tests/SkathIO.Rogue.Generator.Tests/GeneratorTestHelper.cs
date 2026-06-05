using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SkathIO.Rogue.SourceGenerator;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

internal static class GeneratorTestHelper
{
    /// <summary>
    /// Runs the <see cref="RogueGenerator"/> against the given source code and returns the result.
    /// The compilation includes references to <c>SkathIO.Rogue.Abstractions</c> automatically.
    /// </summary>
    internal static GeneratorDriverRunResult RunGenerator(string source)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        MetadataReference[] references = GetBaseReferences();

        CSharpCompilation compilation =
            CSharpCompilation.Create(
                assemblyName: "TestAssembly",
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        CSharpGeneratorDriver driver = CreateDriver();
        driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);
        return driver.GetRunResult();
    }

    /// <summary>
    /// Creates a generator driver with incremental step tracking enabled so tests can
    /// assert on cached/unchanged steps (NFR-MAINT-4).
    /// </summary>
    internal static CSharpGeneratorDriver CreateDriver()
    {
        RogueGenerator generator = new RogueGenerator();
        return CSharpGeneratorDriver.Create(
            generators: new[] { generator.AsSourceGenerator() },
            additionalTexts: System.Collections.Immutable.ImmutableArray<AdditionalText>.Empty,
            parseOptions: null,
            optionsProvider: null,
            driverOptions: new GeneratorDriverOptions(
                disabledOutputs: IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));
    }

    /// <summary>
    /// Runs the generator's discovery phase directly against a compilation built from
    /// <paramref name="source"/> and returns the extracted models. Asserts the input
    /// compilation has no C# compile errors first.
    /// </summary>
    internal static DiscoveredModels ExtractModels(string source)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation =
            CSharpCompilation.Create(
                assemblyName: "TestAssembly",
                syntaxTrees: new[] { syntaxTree },
                references: GetBaseReferences(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compileErrors = compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(compileErrors);

        return RogueGenerator.ExtractFromCompilation(compilation);
    }

    /// <summary>Runs the generator and asserts no generator exception was thrown.</summary>
    internal static GeneratorDriverRunResult RunGeneratorAndAssertClean(string source)
    {
        GeneratorDriverRunResult result = RunGenerator(source);
        Assert.Empty(result.Results.Where(static r => r.Exception is not null));
        return result;
    }

    /// <summary>
    /// Runs the generator and returns the driver and initial compilation so callers can
    /// perform incremental re-runs.
    /// </summary>
    internal static GeneratorDriverRunResult RunGeneratorWithReason(
        string source,
        out CSharpGeneratorDriver initialDriver,
        out CSharpCompilation initialCompilation)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        MetadataReference[] references = GetBaseReferences();

        initialCompilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        initialDriver = CreateDriver();
        initialDriver = (CSharpGeneratorDriver)initialDriver.RunGenerators(initialCompilation);
        return initialDriver.GetRunResult();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────

    private static MetadataReference[] GetBaseReferences()
    {
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        return new MetadataReference[]
        {
            // mscorlib / System.Private.CoreLib
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            // System.Runtime
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            // System.Collections (ImmutableArray lives here transitively)
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
            // System.Threading.Tasks (ValueTask, Task)
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Threading.Tasks.dll")),
            // System.Linq (used by test source snippets)
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Linq.dll")),
            // SkathIO.Rogue.Abstractions — the contracts the generator discovers
            MetadataReference.CreateFromFile(typeof(SkathIO.Rogue.IRequest<>).Assembly.Location),
        };
    }
}
