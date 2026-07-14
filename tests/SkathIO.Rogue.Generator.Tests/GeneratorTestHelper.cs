using System.IO;
using System.Linq;
using System.Reflection;
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
    /// Runs the <see cref="RogueGenerator"/> against <paramref name="source"/> with additional
    /// <see cref="MetadataReference"/>s beyond the base set — used to simulate a directly-referenced
    /// assembly (e.g. a compiled behavior-only package like
    /// <c>SkathIO.Rogue.Validation.FluentValidation.dll</c>) so the PD-17 metadata behavior scan has
    /// something to discover without needing a real second csproj. Build the reference itself with
    /// <see cref="EmitToMetadataReference"/>.
    /// </summary>
    internal static GeneratorDriverRunResult RunGenerator(string source, params MetadataReference[] extraReferences)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        MetadataReference[] references = GetBaseReferences().Concat(extraReferences).ToArray();

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

    /// <summary>Runs the generator (with extra references) and asserts no generator exception was thrown.</summary>
    internal static GeneratorDriverRunResult RunGeneratorAndAssertClean(string source, params MetadataReference[] extraReferences)
    {
        GeneratorDriverRunResult result = RunGenerator(source, extraReferences);
        Assert.Empty(result.Results.Where(static r => r.Exception is not null));
        return result;
    }

    /// <summary>
    /// Compiles <paramref name="source"/> (against the base references) to an in-memory PE image and
    /// wraps it as a <see cref="MetadataReference"/>. Simulates a directly-referenced compiled
    /// assembly — e.g. a behavior-only package the generator's PD-17 metadata scan must walk — the
    /// scan only cares that the type is visible via <c>MetadataReference</c>, not how the assembly was
    /// produced, so an in-memory emit stands in for a real second csproj in tests.
    /// </summary>
    internal static MetadataReference EmitToMetadataReference(string source, string assemblyName)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: new[] { syntaxTree },
            references: GetBaseReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        Assert.True(
            emitResult.Success,
            "Reference-library emit failed. Diagnostics:\n" +
            string.Join("\n", emitResult.Diagnostics
                .Where(static d => d.Severity == DiagnosticSeverity.Error)
                .Select(static d => d.ToString())));

        ms.Seek(0, SeekOrigin.Begin);
        return MetadataReference.CreateFromImage(ms.ToArray());
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
    /// Runs the generator over <paramref name="source"/>, then feeds the consumer source PLUS every
    /// generated source back into a fresh <see cref="CSharpCompilation"/> (referencing the runtime
    /// DLL + DI abstractions) and asserts the combined compilation has zero <see
    /// cref="DiagnosticSeverity.Error"/> diagnostics.
    ///
    /// This is the gate that <c>RunGeneratorAndAssertClean</c> (generator-didn't-throw only) cannot
    /// provide: the generated dispatcher can be valid-as-text yet fail to compile in the consumer's
    /// project (e.g. CS0103 from an out-of-scope local — defect #1, review 2026-06-07). Without this
    /// check that class of emitter bug ships as a build break in downstream consumers.
    /// </summary>
    internal static CSharpCompilation RunGeneratorAndAssertCompiles(string source)
    {
        GeneratorDriverRunResult runResult = RunGenerator(source);
        Assert.Empty(runResult.Results.Where(static r => r.Exception is not null));

        var trees = new System.Collections.Generic.List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(source),
        };
        foreach (var genSource in runResult.Results.SelectMany(r => r.GeneratedSources))
        {
            trees.Add(CSharpSyntaxTree.ParseText(genSource.SourceText));
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "CompileVerificationAssembly",
            syntaxTrees: trees,
            references: GetRuntimeReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(
            errors.Count == 0,
            "Generated dispatcher failed to compile. Diagnostics:\n" +
            string.Join("\n", errors.Select(static d => d.ToString())));

        return compilation;
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

    /// <summary>
    /// Runs the generator over <paramref name="source"/>, compiles the consumer source + generated
    /// sources to an in-memory assembly, loads it, and returns the loaded <see cref="Assembly"/>.
    /// Asserts zero Error-severity diagnostics on emit. Lets a test build a real DI container from the
    /// generated registration and dispatch a request to observe *runtime* behavior (e.g. that an
    /// exception action fires exactly once — defect #2, review 2026-06-07, which compile-cleanliness
    /// alone cannot catch).
    /// </summary>
    internal static Assembly EmitAndLoadAssembly(string source)
    {
        GeneratorDriverRunResult runResult = RunGenerator(source);
        Assert.Empty(runResult.Results.Where(static r => r.Exception is not null));

        var trees = new System.Collections.Generic.List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(source),
        };
        foreach (var genSource in runResult.Results.SelectMany(r => r.GeneratedSources))
        {
            trees.Add(CSharpSyntaxTree.ParseText(genSource.SourceText));
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "RuntimeVerificationAssembly_" + System.Guid.NewGuid().ToString("N"),
            syntaxTrees: trees,
            references: GetRuntimeReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        Assert.True(
            emitResult.Success,
            "Emit failed. Diagnostics:\n" +
            string.Join("\n", emitResult.Diagnostics
                .Where(static d => d.Severity == DiagnosticSeverity.Error)
                .Select(static d => d.ToString())));

        ms.Seek(0, SeekOrigin.Begin);
        return Assembly.Load(ms.ToArray());
    }

    /// <summary>
    /// Builds a real DI container that wires the loaded assembly's generated registration through the
    /// production path: forces the assembly's module initializer to run (which appends the assembly's
    /// registrar to the <c>RogueRegistrationBridge</c> registry via <c>Register</c> — PD-15/PD-33), then
    /// calls <c>AddRogue</c>, which registers <c>ISender</c>/<c>IMediator</c> and invokes the bridge to
    /// register the generated handlers/processors/dispatcher. This exercises the same wiring a consumer's
    /// app would.
    /// <para>
    /// The bridge registry is process-global and append-only (PD-33), so registrars from every
    /// previously-loaded generated assembly in this test run accumulate in it. Without isolation, this
    /// call's <c>AddRogue</c> would invoke those stale registrars too — and PD-38's first-wins
    /// <c>TryAddScoped&lt;RogueDispatcher,...&gt;</c> could let an earlier assembly's empty/foreign
    /// <c>RogueDispatcherImpl</c> shadow this assembly's, so this request would not dispatch. We therefore
    /// snapshot the registry, reset it to contain only this assembly's registrar, build the provider, then
    /// restore the snapshot (so we neither see nor leak other cases' registrars).
    /// </para>
    /// </summary>
    internal static System.IServiceProvider BuildProviderFromGenerated(Assembly assembly)
    {
        var saved = SkathIO.Rogue.RogueRegistrationBridge.SnapshotRegistrars();
        try
        {
            // Reset to a known-empty baseline, then run THIS assembly's [ModuleInitializer] so the only
            // registrar in the bridge is this assembly's (RunModuleConstructor is a one-shot no-op if
            // already run, but each EmitAndLoadAssembly call produces a fresh, never-initialized module).
            SkathIO.Rogue.RogueRegistrationBridge.RestoreRegistrars(
                System.Array.Empty<System.Action<
                    Microsoft.Extensions.DependencyInjection.IServiceCollection, SkathIO.Rogue.RogueOptions>>());

            var anyGeneratedType = assembly.GetType("SkathIO.Rogue.Generated.RogueGeneratedRegistration", throwOnError: true)!;
            System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(anyGeneratedType.Module.ModuleHandle);

            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            SkathIO.Rogue.RogueServiceCollectionExtensions.AddRogue(services);

            return Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
                .BuildServiceProvider(services);
        }
        finally
        {
            SkathIO.Rogue.RogueRegistrationBridge.RestoreRegistrars(saved);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Base references plus the runtime DLL (<c>SkathIO.Rogue</c>) and the DI abstractions, so the
    /// generated dispatcher — which calls <c>PipelineExecutor.Execute</c>, derives from
    /// <c>RogueDispatcher</c>, and resolves services via <c>ServiceProviderServiceExtensions</c> —
    /// has every type it references available at compile time.
    /// </summary>
    private static MetadataReference[] GetRuntimeReferences()
    {
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        var refs = new System.Collections.Generic.List<MetadataReference>(GetBaseReferences())
        {
            // SkathIO.Rogue runtime DLL: PipelineExecutor, RogueDispatcher, RequestHandlerDelegate impl path.
            MetadataReference.CreateFromFile(typeof(SkathIO.Rogue.PipelineExecutor).Assembly.Location),
            // Microsoft.Extensions.DependencyInjection.Abstractions: ServiceProviderServiceExtensions, GetServices.
            MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions).Assembly.Location),
            // System.Linq.Expressions etc. live alongside the runtime; pull the full netstandard facade in.
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "netstandard.dll")),
            // IServiceProvider is type-forwarded to System.ComponentModel on net10.
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.ComponentModel.dll")),
            // System.Diagnostics.DiagnosticSource: RogueTelemetry's DispatchScope/StartDispatch signatures
            // surface Activity, so the generated dispatcher's telemetry call sites need this transitively
            // at emit time (CS0012 otherwise — e.g. on the notification/Publish path).
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Diagnostics.DiagnosticSource.dll")),
        };

        return refs.ToArray();
    }

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
            MetadataReference.CreateFromFile(typeof(SkathIO.Rogue.ICommand<>).Assembly.Location),
        };
    }
}
