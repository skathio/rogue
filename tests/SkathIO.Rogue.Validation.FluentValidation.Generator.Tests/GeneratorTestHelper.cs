using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SkathIO.Rogue.Validation.FluentValidation.SourceGenerator;
using Xunit;

namespace SkathIO.Rogue.Validation.FluentValidation.Generator.Tests;

internal static class GeneratorTestHelper
{
    /// <summary>
    /// Runs the discovery pipeline directly against a compilation built from <paramref name="source"/>
    /// and returns the extracted models. Asserts the input compilation has no C# compile errors first.
    /// Mirrors <c>SkathIO.Rogue.Generator.Tests/GeneratorTestHelper.ExtractModels</c>.
    /// </summary>
    internal static DiscoveredValidators ExtractModels(string source)
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

        return RogueFluentValidationGenerator.ExtractFromCompilation(compilation);
    }

    /// <summary>
    /// Runs the full <see cref="RogueFluentValidationGenerator"/> (via a real
    /// <see cref="CSharpGeneratorDriver"/>) against <paramref name="source"/> and returns the result.
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

    /// <summary>Runs the generator and asserts no generator exception was thrown.</summary>
    internal static GeneratorDriverRunResult RunGeneratorAndAssertClean(string source)
    {
        GeneratorDriverRunResult result = RunGenerator(source);
        Assert.Empty(result.Results.Where(static r => r.Exception is not null));
        return result;
    }

    /// <summary>
    /// Creates a generator driver with incremental step tracking enabled so tests can assert on
    /// cached/unchanged steps.
    /// </summary>
    internal static CSharpGeneratorDriver CreateDriver()
    {
        RogueFluentValidationGenerator generator = new RogueFluentValidationGenerator();
        return CSharpGeneratorDriver.Create(
            generators: new[] { generator.AsSourceGenerator() },
            additionalTexts: System.Collections.Immutable.ImmutableArray<AdditionalText>.Empty,
            parseOptions: null,
            optionsProvider: null,
            driverOptions: new GeneratorDriverOptions(
                disabledOutputs: IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));
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
            // System.Linq.Expressions — FluentValidation's RuleFor(x => x.Prop) takes an
            // Expression<Func<T, TProp>>, so validator fixtures need this to compile.
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Linq.Expressions.dll")),
            // FluentValidation — the source of IValidator<T>/AbstractValidator<T> the generator
            // discovers. Fully qualified with global:: to avoid this project's own
            // "SkathIO.Rogue.Validation.FluentValidation" namespace chain shadowing the real one.
            MetadataReference.CreateFromFile(typeof(global::FluentValidation.IValidator<>).Assembly.Location),
        };
    }
}
