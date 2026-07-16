using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
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
    /// Runs the generator over <paramref name="source"/>, compiles the consumer source + generated
    /// sources to an in-memory assembly (referencing the real <c>SkathIO.Rogue</c> runtime DLL so the
    /// emitted module initializer / registration class actually resolve), loads it, and returns the
    /// loaded <see cref="Assembly"/>. Asserts zero Error-severity diagnostics on emit. Mirrors
    /// <c>SkathIO.Rogue.Generator.Tests/GeneratorTestHelper.EmitAndLoadAssembly</c> — lets a test build
    /// a real DI container from the generated registration and observe *runtime* behavior, not just
    /// generated-text shape.
    /// </summary>
    internal static Assembly EmitAndLoadAssembly(string source)
    {
        GeneratorDriverRunResult runResult = RunGenerator(source);
        Assert.Empty(runResult.Results.Where(static r => r.Exception is not null));

        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source) };
        foreach (var genSource in runResult.Results.SelectMany(r => r.GeneratedSources))
        {
            trees.Add(CSharpSyntaxTree.ParseText(genSource.SourceText));
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "FluentValidationRuntimeVerificationAssembly_" + Guid.NewGuid().ToString("N"),
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
    /// Builds a real DI container that wires <paramref name="assembly"/>'s generated FluentValidation
    /// registration through the production path: forces the assembly's module initializer to run
    /// (which appends this assembly's registrar to the process-global, append-only
    /// <see cref="SkathIO.Rogue.RogueRegistrationBridge"/> — the exact mechanism D5's fully-implicit
    /// discovery relies on), then calls plain <c>AddRogue()</c> — no explicit validator registration
    /// of any kind. Builds the provider with <c>ValidateScopes</c>/<c>ValidateOnBuild</c> (mirrors
    /// <c>SkathIO.Rogue.DiResolution.Tests</c>' container-boundary strictness).
    /// <para>
    /// The bridge registry is process-global and append-only (PD-33), so registrars from every
    /// previously-loaded generated assembly in this test run would otherwise accumulate. We
    /// snapshot the registry, reset it to empty, run this assembly's module initializer alone, build
    /// the provider, then restore the snapshot — mirroring the core generator test suite's own
    /// isolation pattern exactly (<c>SkathIO.Rogue.Generator.Tests/GeneratorTestHelper.BuildProviderFromGenerated</c>).
    /// </para>
    /// </summary>
    internal static (IServiceCollection Services, IServiceProvider Provider) BuildProviderFromGenerated(
        Assembly assembly, Action<SkathIO.Rogue.RogueOptions>? configure = null)
    {
        var saved = SkathIO.Rogue.RogueRegistrationBridge.SnapshotRegistrars();
        try
        {
            SkathIO.Rogue.RogueRegistrationBridge.RestoreRegistrars(
                Array.Empty<Action<IServiceCollection, SkathIO.Rogue.RogueOptions>>());

            // The registration class is always emitted (even for zero validators), so it is a safe
            // anchor type whose module we can force-run regardless of the fixture's shape.
            var anchorType = assembly.GetType(
                "SkathIO.Rogue.Validation.FluentValidation.Generated.RogueFluentValidationRegistration",
                throwOnError: true)!;
            RuntimeHelpers.RunModuleConstructor(anchorType.Module.ModuleHandle);

            var services = new ServiceCollection();
            SkathIO.Rogue.RogueServiceCollectionExtensions.AddRogue(services, configure);

            var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });

            return (services, provider);
        }
        finally
        {
            SkathIO.Rogue.RogueRegistrationBridge.RestoreRegistrars(saved);
        }
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

    /// <summary>
    /// Base references plus the real <c>SkathIO.Rogue</c> runtime DLL (<c>RogueOptions</c>,
    /// <c>RogueRegistrationBridge</c>, <c>AddRogue</c>) and the DI abstractions/container packages —
    /// so the emitted registration + module-init sources (which call
    /// <c>ServiceCollectionDescriptorExtensions.TryAddEnumerable</c> and
    /// <c>RogueRegistrationBridge.Register</c>) resolve, and so <see cref="BuildProviderFromGenerated"/>
    /// can build a real <see cref="IServiceProvider"/>. Mirrors
    /// <c>SkathIO.Rogue.Generator.Tests/GeneratorTestHelper.GetRuntimeReferences</c>.
    /// </summary>
    private static MetadataReference[] GetRuntimeReferences()
    {
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        var refs = new List<MetadataReference>(GetBaseReferences())
        {
            // SkathIO.Rogue runtime DLL: RogueOptions, RogueRegistrationBridge, AddRogue.
            MetadataReference.CreateFromFile(typeof(SkathIO.Rogue.RogueOptions).Assembly.Location),
            // Microsoft.Extensions.DependencyInjection.Abstractions: IServiceCollection,
            // ServiceDescriptor, ServiceCollectionDescriptorExtensions.TryAddEnumerable.
            MetadataReference.CreateFromFile(typeof(ServiceDescriptor).Assembly.Location),
            // netstandard.dll facade + type-forwarded BCL pieces the runtime DLL's own public surface
            // transitively touches (IServiceProvider forwards to System.ComponentModel on net10;
            // RogueTelemetry's DispatchScope surfaces System.Diagnostics.Activity).
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "netstandard.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.ComponentModel.dll")),
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
