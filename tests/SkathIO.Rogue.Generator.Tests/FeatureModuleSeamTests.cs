using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using SkathIO.Rogue.SourceGenerator;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// How a request carrying a feature-module marker is emitted: its typed Send_X calls one partial
/// pre-handler hook per marked module before the handler is resolved, the hook is declared on a
/// partial dispatcher class, and a module's implementing part runs ahead of the handler. Also covers
/// the version parsing behind ROGUE013.
/// </summary>
[Collection(RealDiDispatchCollection.Name)] // shares the process-global registration bridge
public sealed class FeatureModuleSeamTests
{
    private const string Markers = @"
namespace SkathIO.Rogue.Validation
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public sealed class ValidateAttribute : System.Attribute { }
}
namespace SkathIO.Rogue.Jobs
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public sealed class JobAttribute : System.Attribute { }
}
";

    private const string MarkedRequest = @"
using SkathIO.Rogue;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public static class Recorder
{
    public static readonly List<string> Steps = new List<string>();
}

[SkathIO.Rogue.Validation.Validate]
[SkathIO.Rogue.Jobs.Job]
public sealed partial class PingRequest : ICommand<string> { }

public sealed class PingHandler : ICommandHandler<PingRequest, string>
{
    public ValueTask<string> Handle(PingRequest request, CancellationToken ct)
    {
        Recorder.Steps.Add(""handler"");
        return ValueTask.FromResult(""pong"");
    }
}
";

    // Stands in for the code a feature module will generate: the implementing part of a hook.
    private const string ValidationHookImplementation = @"
namespace SkathIO.Rogue.Generated
{
    internal sealed partial class RogueDispatcherImpl
    {
        partial void PreHandle_Validation_Send_PingRequest(global::PingRequest request, global::System.Threading.CancellationToken cancellationToken)
        {
            global::Recorder.Steps.Add(""validation"");
        }
    }
}
";

    [Fact]
    public void MarkedRequest_CallsOneHookPerModule_BeforeResolvingTheHandler()
    {
        string dispatcher = Dispatcher(MarkedRequest + Markers);

        Assert.Contains("internal sealed partial class RogueDispatcherImpl", dispatcher);
        Assert.Contains("partial void PreHandle_Validation_Send_PingRequest(global::PingRequest request, global::System.Threading.CancellationToken cancellationToken);", dispatcher);
        Assert.Contains("partial void PreHandle_Jobs_Send_PingRequest(global::PingRequest request, global::System.Threading.CancellationToken cancellationToken);", dispatcher);

        int sendStart = dispatcher.IndexOf("Send_PingRequest(global::PingRequest request, global::System.Threading.CancellationToken cancellationToken)\n", System.StringComparison.Ordinal);
        int validation = dispatcher.IndexOf("PreHandle_Validation_Send_PingRequest(request, cancellationToken);", sendStart, System.StringComparison.Ordinal);
        int jobs = dispatcher.IndexOf("PreHandle_Jobs_Send_PingRequest(request, cancellationToken);", sendStart, System.StringComparison.Ordinal);
        int resolve = dispatcher.IndexOf("var handler = ", sendStart, System.StringComparison.Ordinal);
        Assert.True(sendStart >= 0 && validation > sendStart && jobs > validation && resolve > jobs,
            "Expected the Validation then Jobs hooks ahead of handler resolution in Send_PingRequest.");

        // The marked request keeps the direct-handler bypass: no behavior list is looked up.
        Assert.Contains("return Send_PingRequest_Direct(handler, request, cancellationToken);", dispatcher);
    }

    [Fact]
    public void MarkedRequest_WithoutAModuleImplementation_Compiles()
    {
        GeneratorTestHelper.RunGeneratorAndAssertCompiles(MarkedRequest + Markers);
    }

    [Fact]
    public async Task MarkedRequest_ModuleHook_RunsBeforeTheHandler()
    {
        Assembly assembly = GeneratorTestHelper.EmitAndLoadAssembly(MarkedRequest + Markers + ValidationHookImplementation);
        System.IServiceProvider provider = GeneratorTestHelper.BuildProviderFromGenerated(assembly);
        var sender = (ISender)Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService(provider, typeof(ISender));
        var request = (ICommand<string>)System.Activator.CreateInstance(assembly.GetType("PingRequest", throwOnError: true)!)!;

        string result = await sender.Send(request, CancellationToken.None);

        Assert.Equal("pong", result);
        var steps = (System.Collections.Generic.List<string>)assembly.GetType("Recorder", throwOnError: true)!
            .GetField("Steps", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(new[] { "validation", "handler" }, steps);
    }

    [Fact]
    public void AttributeWithTheMarkerNameInAnotherNamespace_DoesNotMarkTheRequest()
    {
        const string source = @"
namespace Other
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public sealed class ValidateAttribute : System.Attribute { }
}
[Other.Validate]
public sealed class PingRequest : SkathIO.Rogue.ICommand<string> { }
public sealed class PingHandler : SkathIO.Rogue.ICommandHandler<PingRequest, string>
{
    public System.Threading.Tasks.ValueTask<string> Handle(PingRequest request, System.Threading.CancellationToken ct)
        => new System.Threading.Tasks.ValueTask<string>(""pong"");
}
";
        string dispatcher = Dispatcher(source);

        Assert.Contains("internal sealed class RogueDispatcherImpl", dispatcher);
        Assert.DoesNotContain("PreHandle_", dispatcher);
    }

    [Fact]
    public void UnresolvedMarkerAttribute_DoesNotMarkTheRequest()
    {
        // No marker type in the compilation: the attribute binds to an error type, so no module is on.
        string dispatcher = Dispatcher(@"
[SkathIO.Rogue.Validation.Validate]
public sealed class PingRequest : SkathIO.Rogue.ICommand<string> { }
public sealed class PingHandler : SkathIO.Rogue.ICommandHandler<PingRequest, string>
{
    public System.Threading.Tasks.ValueTask<string> Handle(PingRequest request, System.Threading.CancellationToken ct)
        => new System.Threading.Tasks.ValueTask<string>(""pong"");
}
");

        Assert.Contains("internal sealed class RogueDispatcherImpl", dispatcher);
        Assert.DoesNotContain("PreHandle_", dispatcher);
    }

    [Theory]
    [InlineData("2.1", 2, 1)]
    [InlineData("2.1.0", 2, 1)]
    [InlineData("10.20.3.0", 10, 20)]
    [InlineData("2.1.0-alpha.0.4+abc123", 2, 1)]
    [InlineData("2.1+abc123", 2, 1)]
    public void TryParseMajorMinor_ReadsTheLeadingMajorMinor(string version, int major, int minor)
    {
        Assert.True(RogueGenerator.TryParseMajorMinor(version, out int actualMajor, out int actualMinor));
        Assert.Equal(major, actualMajor);
        Assert.Equal(minor, actualMinor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("2.")]
    [InlineData(".1")]
    [InlineData("x.1")]
    [InlineData("2.x")]
    [InlineData("-2.1")]
    [InlineData("99999999999.1")]
    public void TryParseMajorMinor_RejectsAnUnparsableVersion(string? version)
    {
        Assert.False(RogueGenerator.TryParseMajorMinor(version, out _, out _));
    }

    [Fact]
    public void Rogue013_UsesTheContractsVersion_WhenOnlyTheContractsAreReferenced()
    {
        // The base references hold SkathIO.Rogue.Abstractions but not the runtime assembly.
        MetadataReference feature = GeneratorTestHelper.EmitToMetadataReference(
            "[assembly: System.Reflection.AssemblyFileVersion(\"999.0.0.0\")]", "SkathIO.Rogue.Jobs");

        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean("public sealed class Empty { }", feature);

        Diagnostic diagnostic = Assert.Single(result.Results.SelectMany(static r => r.Diagnostics), static d => d.Id == "ROGUE013");
        Assert.Contains("'SkathIO.Rogue.Jobs' is version 999.0.0.0", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Rogue013_IgnoresAnUnrelatedRogueAssembly()
    {
        MetadataReference other = GeneratorTestHelper.EmitToMetadataReference(
            "[assembly: System.Reflection.AssemblyFileVersion(\"999.0.0.0\")]", "SkathIO.Rogue.Validation.FluentValidation");

        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean("public sealed class Empty { }", other);

        Assert.DoesNotContain(result.Results.SelectMany(static r => r.Diagnostics), static d => d.Id == "ROGUE013");
    }

    private static string Dispatcher(string source)
    {
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        return result.Results.SelectMany(static r => r.GeneratedSources)
            .Single(static s => s.HintName == "RogueDispatcher.g.cs")
            .SourceText.ToString().Replace("\r\n", "\n", System.StringComparison.Ordinal);
    }
}
