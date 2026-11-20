using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// Holds the feature-module seam of the core generator to its rules. A feature module switches on
/// when its marker attribute type exists in the compilation; a request that carries no marker must
/// keep the zero-allocation direct-handler path and be emitted exactly as it was before the seam
/// existed; a feature package whose major.minor version differs from the core package's raises the
/// ROGUE013 warning.
/// </summary>
public sealed class FeatureModuleTests
{
    private const string DispatcherHint = "RogueDispatcher.g.cs";
    private const string Rogue013 = "ROGUE013";

    private static readonly string[] _featurePackages =
    {
        "SkathIO.Rogue.Validation",
        "SkathIO.Rogue.Jobs",
        "SkathIO.Rogue.Messaging",
    };

    // An unmarked response command and an unmarked void command, with no behaviors or processors, so
    // both take the direct-handler bypass.
    private const string UnmarkedRequests = @"
namespace App
{
    public sealed record CreateOrder(string Sku) : global::SkathIO.Rogue.ICommand<int>;
    public sealed class CreateOrderHandler : global::SkathIO.Rogue.ICommandHandler<CreateOrder, int>
    {
        public global::System.Threading.Tasks.ValueTask<int> Handle(CreateOrder command, global::System.Threading.CancellationToken cancellationToken)
            => new global::System.Threading.Tasks.ValueTask<int>(1);
    }

    public sealed record DeleteOrder(int Id) : global::SkathIO.Rogue.ICommand;
    public sealed class DeleteOrderHandler : global::SkathIO.Rogue.ICommandHandler<DeleteOrder>
    {
        public global::System.Threading.Tasks.ValueTask Handle(DeleteOrder command, global::System.Threading.CancellationToken cancellationToken)
            => default;
    }
}
";

    // The marker attributes named by the request ([Validate], [Job], [Message]), declared in source
    // because no feature package exists yet. They are declared both in each package's namespace and
    // in the core namespace so the module switches on wherever the builder places its marker.
    private const string MarkerTypes = @"
namespace SkathIO.Rogue.Validation
{
    [global::System.AttributeUsage(global::System.AttributeTargets.Class)]
    public sealed class ValidateAttribute : global::System.Attribute { }
}
namespace SkathIO.Rogue.Jobs
{
    [global::System.AttributeUsage(global::System.AttributeTargets.Class)]
    public sealed class JobAttribute : global::System.Attribute { }
}
namespace SkathIO.Rogue.Messaging
{
    [global::System.AttributeUsage(global::System.AttributeTargets.Class)]
    public sealed class MessageAttribute : global::System.Attribute
    {
        public MessageAttribute() { }
        public MessageAttribute(string name) { }
    }
}
namespace SkathIO.Rogue
{
    [global::System.AttributeUsage(global::System.AttributeTargets.Class)]
    public sealed class ValidateAttribute : global::System.Attribute { }
    [global::System.AttributeUsage(global::System.AttributeTargets.Class)]
    public sealed class JobAttribute : global::System.Attribute { }
}
";

    // A sibling request that carries the markers, next to the unmarked ones.
    private const string MarkedSibling = @"
namespace App
{
    [global::SkathIO.Rogue.Validation.Validate]
    [global::SkathIO.Rogue.Validate]
    [global::SkathIO.Rogue.Jobs.Job]
    [global::SkathIO.Rogue.Job]
    public sealed partial record ShipOrder(int Id) : global::SkathIO.Rogue.ICommand<bool>;
    public sealed class ShipOrderHandler : global::SkathIO.Rogue.ICommandHandler<ShipOrder, bool>
    {
        public global::System.Threading.Tasks.ValueTask<bool> Handle(ShipOrder command, global::System.Threading.CancellationToken cancellationToken)
            => new global::System.Threading.Tasks.ValueTask<bool>(true);
    }
}
";

    // What the generator emitted for the unmarked requests before the feature-module seam existed.
    private const string CreateOrderBefore = @"        internal global::System.Threading.Tasks.ValueTask<global::System.Int32> Send_App_CreateOrder(global::App.CreateOrder request, global::System.Threading.CancellationToken cancellationToken)
        {
            var handler = global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::SkathIO.Rogue.ICommandHandler<global::App.CreateOrder, global::System.Int32>>(_serviceProvider);
            var __scope = global::SkathIO.Rogue.RogueTelemetry.StartDispatch<global::App.CreateOrder>();
            if (__scope is null)
            {
                return Send_App_CreateOrder_Direct(handler, request, cancellationToken);
            }

            return Send_App_CreateOrder_DirectWithTelemetry(handler, request, cancellationToken, __scope.Value);
        }
        private static global::System.Threading.Tasks.ValueTask<global::System.Int32> Send_App_CreateOrder_Direct(global::SkathIO.Rogue.ICommandHandler<global::App.CreateOrder, global::System.Int32> handler, global::App.CreateOrder request, global::System.Threading.CancellationToken cancellationToken)
        {
            return handler.Handle(request, cancellationToken);
        }
        private static async global::System.Threading.Tasks.ValueTask<global::System.Int32> Send_App_CreateOrder_DirectWithTelemetry(global::SkathIO.Rogue.ICommandHandler<global::App.CreateOrder, global::System.Int32> handler, global::App.CreateOrder request, global::System.Threading.CancellationToken cancellationToken, global::SkathIO.Rogue.DispatchScope scope)
        {
            global::System.Exception? __exc = null;
            try
            {
                return await Send_App_CreateOrder_Direct(handler, request, cancellationToken).ConfigureAwait(false);
            }
            catch (global::System.Exception __ex)
            {
                __exc = __ex;
                throw;
            }
            finally
            {
                global::SkathIO.Rogue.RogueTelemetry.StopDispatch(scope, __exc);
            }
        }";

    private const string DeleteOrderBefore = @"        internal global::System.Threading.Tasks.ValueTask<global::SkathIO.Rogue.Unit> Send_App_DeleteOrder(global::App.DeleteOrder request, global::System.Threading.CancellationToken cancellationToken)
        {
            var handler = global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::SkathIO.Rogue.ICommandHandler<global::App.DeleteOrder>>(_serviceProvider);
            var __scope = global::SkathIO.Rogue.RogueTelemetry.StartDispatch<global::App.DeleteOrder>();
            if (__scope is null)
            {
                return Send_App_DeleteOrder_Direct(handler, request, cancellationToken);
            }

            return Send_App_DeleteOrder_DirectWithTelemetry(handler, request, cancellationToken, __scope.Value);
        }
        private static global::System.Threading.Tasks.ValueTask<global::SkathIO.Rogue.Unit> Send_App_DeleteOrder_Direct(global::SkathIO.Rogue.ICommandHandler<global::App.DeleteOrder> handler, global::App.DeleteOrder request, global::System.Threading.CancellationToken cancellationToken)
        {
            var voidVt = handler.Handle(request, cancellationToken);
            if (voidVt.IsCompletedSuccessfully) return global::SkathIO.Rogue.Unit.Task;
            return AwaitVoidThenUnit(voidVt);
        }
        private static async global::System.Threading.Tasks.ValueTask<global::SkathIO.Rogue.Unit> Send_App_DeleteOrder_DirectWithTelemetry(global::SkathIO.Rogue.ICommandHandler<global::App.DeleteOrder> handler, global::App.DeleteOrder request, global::System.Threading.CancellationToken cancellationToken, global::SkathIO.Rogue.DispatchScope scope)
        {
            global::System.Exception? __exc = null;
            try
            {
                return await Send_App_DeleteOrder_Direct(handler, request, cancellationToken).ConfigureAwait(false);
            }
            catch (global::System.Exception __ex)
            {
                __exc = __ex;
                throw;
            }
            finally
            {
                global::SkathIO.Rogue.RogueTelemetry.StopDispatch(scope, __exc);
            }
        }";

    public static TheoryData<string> FeaturePackages()
    {
        var data = new TheoryData<string>();
        foreach (string name in _featurePackages)
        {
            data.Add(name);
        }
        return data;
    }

    public static TheoryData<string, VersionShift> MismatchedVersions()
    {
        var data = new TheoryData<string, VersionShift>();
        foreach (string name in _featurePackages)
        {
            data.Add(name, VersionShift.MinorUp);
            data.Add(name, VersionShift.MajorUp);
            data.Add(name, VersionShift.MajorDown);
        }
        return data;
    }

    public static TheoryData<string, VersionShift> MatchedVersions()
    {
        var data = new TheoryData<string, VersionShift>();
        foreach (string name in _featurePackages)
        {
            data.Add(name, VersionShift.None);
            data.Add(name, VersionShift.PatchUp);
        }
        return data;
    }

    // ── Unmarked requests are emitted exactly as before ──────────────────────────────

    [Fact]
    public void UnmarkedRequests_WithoutMarkerTypes_AreEmittedAsBefore()
    {
        string dispatcher = DispatcherText(UnmarkedRequests);

        Assert.Equal(Normalize(CreateOrderBefore), SendMembers(dispatcher, "Send_App_CreateOrder"));
        Assert.Equal(Normalize(DeleteOrderBefore), SendMembers(dispatcher, "Send_App_DeleteOrder"));
    }

    [Fact]
    public void UnmarkedRequests_NextToMarkerTypesAndAMarkedRequest_AreEmittedAsBefore()
    {
        string dispatcher = DispatcherText(MarkerTypes + UnmarkedRequests + MarkedSibling);

        Assert.Equal(Normalize(CreateOrderBefore), SendMembers(dispatcher, "Send_App_CreateOrder"));
        Assert.Equal(Normalize(DeleteOrderBefore), SendMembers(dispatcher, "Send_App_DeleteOrder"));
    }

    [Fact]
    public void UnmarkedRequests_NextToMarkerTypes_CompileWithoutGeneratorWarnings()
    {
        string source = MarkerTypes + UnmarkedRequests;

        GeneratorTestHelper.RunGeneratorAndAssertCompiles(source);

        Diagnostic[] warnings = GeneratorDiagnostics(GeneratorTestHelper.RunGeneratorAndAssertClean(source))
            .Where(static d => d.Severity >= DiagnosticSeverity.Warning)
            .ToArray();
        Assert.Empty(warnings);
    }

    // ── ROGUE013: feature package major.minor differs from the core package ──────────

    [Theory]
    [MemberData(nameof(MismatchedVersions))]
    public void Rogue013_IsReported_WhenFeatureMajorOrMinorDiffersFromCore(string package, VersionShift shift)
    {
        Diagnostic[] rogue013 = Rogue013Diagnostics(package, shift);

        Assert.NotEmpty(rogue013);
        Assert.All(rogue013, static d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
    }

    [Theory]
    [MemberData(nameof(MatchedVersions))]
    public void Rogue013_IsNotReported_WhenFeatureMajorMinorMatchesCore(string package, VersionShift shift)
    {
        Assert.Empty(Rogue013Diagnostics(package, shift));
    }

    [Fact]
    public void Rogue013_IsNotReported_WhenNoFeaturePackageIsReferenced()
    {
        Diagnostic[] rogue013 = GeneratorDiagnostics(GeneratorTestHelper.RunGeneratorAndAssertClean(UnmarkedRequests, CoreRuntimeReference()))
            .Where(static d => d.Id == Rogue013)
            .ToArray();

        Assert.Empty(rogue013);
    }

    [Fact]
    public void Rogue013_FollowsTheProjectDiagnosticConventions()
    {
        Diagnostic[] rogue013 = Rogue013Diagnostics("SkathIO.Rogue.Validation", VersionShift.MinorUp);
        Assert.NotEmpty(rogue013);
        DiagnosticDescriptor descriptor = rogue013[0].Descriptor;

        Assert.Equal("SkathIO.Rogue", descriptor.Category);
        Assert.Equal(DiagnosticSeverity.Warning, descriptor.DefaultSeverity);
        Assert.True(descriptor.IsEnabledByDefault);
        Assert.Equal("https://skathio.github.io/rogue/diagnostics/ROGUE013", descriptor.HelpLinkUri);
    }

    [Fact]
    public void Rogue013_HasAnUnshippedAnalyzerReleaseRow()
    {
        string releases = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "SkathIO.Rogue.SourceGenerator", "AnalyzerReleases.Unshipped.md"));

        string[] rows = releases.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(static l => Regex.IsMatch(l, @"^ROGUE013\s*\|\s*SkathIO\.Rogue\s*\|\s*Warning\s*\|\s*\S"))
            .ToArray();
        Assert.Single(rows);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    public enum VersionShift
    {
        None,
        PatchUp,
        MinorUp,
        MajorUp,
        MajorDown,
    }

    private static Diagnostic[] Rogue013Diagnostics(string package, VersionShift shift)
    {
        MetadataReference feature = GeneratorTestHelper.EmitToMetadataReference(
            FeatureAssemblySource(package, shift), package);

        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(
            UnmarkedRequests, CoreRuntimeReference(), feature);

        return GeneratorDiagnostics(result).Where(static d => d.Id == Rogue013).ToArray();
    }

    // The fake feature package carries every version attribute a .NET assembly has, each shifted from
    // the matching attribute of the core package, so the comparison holds whichever one is read.
    private static string FeatureAssemblySource(string package, VersionShift shift)
    {
        Assembly core = typeof(SkathIO.Rogue.ICommand<>).Assembly;
        Version assemblyVersion = core.GetName().Version!;
        Version fileVersion = Version.Parse(core.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        Version informational = LeadingVersion(core.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);

        string marker = package switch
        {
            "SkathIO.Rogue.Validation" => "Validate",
            "SkathIO.Rogue.Jobs" => "Job",
            _ => "Message",
        };

        return
            "[assembly: global::System.Reflection.AssemblyVersion(\"" + Shift(assemblyVersion, shift).ToString(4) + "\")]\n" +
            "[assembly: global::System.Reflection.AssemblyFileVersion(\"" + Shift(fileVersion, shift).ToString(4) + "\")]\n" +
            "[assembly: global::System.Reflection.AssemblyInformationalVersion(\"" + Shift(informational, shift).ToString(3) + "\")]\n" +
            "namespace " + package + "\n{\n" +
            "    [global::System.AttributeUsage(global::System.AttributeTargets.Class)]\n" +
            "    public sealed class " + marker + "Attribute : global::System.Attribute { }\n" +
            "}\n";
    }

    private static Version Shift(Version v, VersionShift shift)
    {
        int build = Math.Max(v.Build, 0);
        int revision = Math.Max(v.Revision, 0);
        Assert.True(v.Major >= 1, "The core package major version must be at least 1 to shift it down.");
        return shift switch
        {
            VersionShift.None => new Version(v.Major, v.Minor, build, revision),
            VersionShift.PatchUp => new Version(v.Major, v.Minor, build + 7, revision),
            VersionShift.MinorUp => new Version(v.Major, v.Minor + 1, 0, 0),
            VersionShift.MajorUp => new Version(v.Major + 1, v.Minor, 0, 0),
            _ => new Version(v.Major - 1, v.Minor, 0, 0),
        };
    }

    private static Version LeadingVersion(string informational)
    {
        Match match = Regex.Match(informational, @"^(\d+)\.(\d+)\.(\d+)");
        Assert.True(match.Success, "Unexpected core informational version: " + informational);
        return new Version(
            int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
            0);
    }

    private static MetadataReference CoreRuntimeReference() =>
        MetadataReference.CreateFromFile(typeof(SkathIO.Rogue.PipelineExecutor).Assembly.Location);

    private static IEnumerable<Diagnostic> GeneratorDiagnostics(GeneratorDriverRunResult result) =>
        result.Results.SelectMany(static r => r.Diagnostics);

    private static string DispatcherText(string source)
    {
        GeneratorDriverRunResult result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        GeneratedSourceResult dispatcher = Assert.Single(
            result.Results.SelectMany(static r => r.GeneratedSources),
            static s => s.HintName == DispatcherHint);
        return Normalize(dispatcher.SourceText.ToString());
    }

    // Returns the dispatcher class members whose name is the request's Send_X method or one of its
    // Send_X_* companions, each from its signature line to its closing brace, blank lines between
    // members dropped.
    private static string SendMembers(string dispatcher, string methodName)
    {
        string[] kept = _member.Matches(dispatcher)
            .Select(static m => m.Value)
            .Where(m => SignatureNames(m, methodName))
            .ToArray();
        return string.Join("\n", kept);
    }

    private static bool SignatureNames(string member, string methodName)
    {
        string signature = member.Substring(0, member.IndexOf('\n', StringComparison.Ordinal));
        return signature.Contains(" " + methodName + "(", StringComparison.Ordinal)
            || signature.Contains(" " + methodName + "_", StringComparison.Ordinal);
    }

    // A class member of the dispatcher: an access-modified signature at member indent, up to the
    // first closing brace back at member indent.
    private static readonly Regex _member = new Regex(
        @"^        (internal|private|public|protected)\b.*?\n        \}$",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkathIO.Rogue.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
