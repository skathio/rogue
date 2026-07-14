using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// Regression coverage for GitHub issue #21: PD-17's metadata behavior scan walks every directly
/// referenced assembly for <c>IPipelineBehavior&lt;,&gt;</c> implementors — including an assembly a
/// compilation only sees because NuGet propagated a <c>PackageReference</c> transitively across a
/// sibling <c>ProjectReference</c> (e.g. a host project <c>MyApp.Api</c> that references
/// <c>MyApp.Application</c>, which itself references
/// <c>SkathIO.Rogue.Validation.FluentValidation</c>). Before the fix, a compilation with zero
/// source-level Rogue declarations but a metadata-discovered behavior still tripped PD-45's
/// <c>HasNothingToRegister</c> gate to "has something," so its module initializer registered an
/// empty <c>RogueDispatcherImpl</c> that could win the cross-assembly <c>TryAddScoped</c> race and
/// shadow the real (handler-bearing) dispatcher, causing a spurious
/// <c>RogueUnregisteredRequestException</c> at runtime.
/// </summary>
[Collection(RealDiDispatchCollection.Name)] // shares the process-global registration bridge — see RealDiDispatchCollection
public sealed class MultiProjectBehaviorSuppressionTests
{
    // Stands in for a compiled behavior-only package, e.g. SkathIO.Rogue.Validation.FluentValidation.dll —
    // a closed IPipelineBehavior<,> with no handlers alongside it.
    private const string BehaviorLibrarySource = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;
public class Ping { }
public sealed class LoggingBehavior : IPipelineBehavior<Ping, string>
{
    public ValueTask<string> Handle(Ping request, RequestHandlerDelegate<string> next, CancellationToken ct) => next();
}";

    [Fact]
    public void HostCompilation_WithOnlyMetadataBehaviorFromReferencedAssembly_SuppressesModuleInit()
    {
        // Simulates MyApp.Api: zero source-level Rogue declarations of its own, but a direct
        // MetadataReference to an assembly that (from MyApp.Api's point of view) just happens to
        // contain a pipeline behavior — exactly what a transitively-propagated PackageReference
        // looks like to the generator. Before the fix, this alone defeated PD-45 suppression.
        MetadataReference behaviorLib =
            GeneratorTestHelper.EmitToMetadataReference(BehaviorLibrarySource, "BehaviorLib");

        const string hostSource = "// MyApp.Api: no handlers, no behaviors, no processors of its own";

        var result = GeneratorTestHelper.RunGeneratorAndAssertClean(hostSource, behaviorLib);
        var moduleInit = result.Results.SelectMany(r => r.GeneratedSources)
            .First(s => s.HintName == "RogueModuleInit.g.cs");
        var text = moduleInit.SourceText.ToString();

        // The host has nothing of its own to contribute — its module initializer must stay
        // suppressed even though the metadata scan found LoggingBehavior in the referenced assembly.
        Assert.DoesNotContain("[global::System.Runtime.CompilerServices.ModuleInitializer]", text);
        Assert.DoesNotContain("RogueRegistrationBridge", text);
    }

    [Fact]
    public void HostCompilation_WithSourceDeclaredBehaviorOnly_StillRegisters()
    {
        // Control case: a project that legitimately declares a behavior in its OWN source (not
        // discovered via metadata), with no handlers of its own, is a genuine "shared behavior"
        // publisher — its module initializer must still register so DI knows about the behavior.
        // The fix only excludes METADATA-sourced behaviors from HasNothingToRegister, not
        // source-declared ones — this pins that the source case is unaffected.
        var result = GeneratorTestHelper.RunGeneratorAndAssertClean(BehaviorLibrarySource);
        var moduleInit = result.Results.SelectMany(r => r.GeneratedSources)
            .First(s => s.HintName == "RogueModuleInit.g.cs");
        var text = moduleInit.SourceText.ToString();

        Assert.Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]", text);
        Assert.Contains("global::SkathIO.Rogue.RogueRegistrationBridge.Register(", text);
    }
}
