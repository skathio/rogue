using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// Holds the 2.0.0 baseline: every runtime package targets net8.0;net10.0 only, the generators stay
/// on netstandard2.0 because Roslyn loads analyzers only from netstandard2.0, and nothing that existed
/// only to serve netstandard2.0 (polyfills, conditional branches, the per-TFM PublicAPI split) is left.
/// </summary>
[Collection(RealDiDispatchCollection.Name)]
public sealed class Net8BaselineTests
{
    private static readonly string[] _runtimePackages =
    {
        "SkathIO.Rogue.Abstractions",
        "SkathIO.Rogue",
        "SkathIO.Rogue.MediatR",
        "SkathIO.Rogue.Logging",
        "SkathIO.Rogue.Validation.FluentValidation",
    };

    private static readonly string[] _roslynComponents =
    {
        "SkathIO.Rogue.SourceGenerator",
        "SkathIO.Rogue.Validation.FluentValidation.SourceGenerator",
        "SkathIO.Rogue.Migration.Analyzer",
    };

    public static TheoryData<string> RuntimePackages()
    {
        var data = new TheoryData<string>();
        foreach (string name in _runtimePackages)
        {
            data.Add(name);
        }
        return data;
    }

    public static TheoryData<string> RoslynComponents()
    {
        var data = new TheoryData<string>();
        foreach (string name in _roslynComponents)
        {
            data.Add(name);
        }
        return data;
    }

    // ── Target frameworks ─────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(RuntimePackages))]
    public void RuntimePackage_TargetsNet8AndNet10Only(string project)
    {
        string[] tfms = TargetFrameworks(project);
        Assert.Equal(new[] { "net10.0", "net8.0" }, tfms.OrderBy(static t => t, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [MemberData(nameof(RuntimePackages))]
    public void RuntimePackage_ProjectFileHasNoNetStandardCondition(string project)
    {
        string text = File.ReadAllText(ProjectFile(project));
        Assert.DoesNotContain("netstandard2.0", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NETSTANDARD2_0", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RuntimePackages))]
    public void RuntimePackage_IsStillPackable(string project)
    {
        XDocument doc = XDocument.Load(ProjectFile(project));
        string? packable = doc.Descendants("IsPackable").Select(static e => e.Value.Trim()).LastOrDefault();
        Assert.Equal("true", packable);
    }

    [Theory]
    [MemberData(nameof(RoslynComponents))]
    public void Generator_StaysOnNetStandard20(string project)
    {
        string[] tfms = TargetFrameworks(project);
        Assert.Equal(new[] { "netstandard2.0" }, tfms);
    }

    // ── Polyfills and conditional branches ────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(RuntimePackages))]
    public void RuntimePackage_HasNoPolyfillsFile(string project)
    {
        string[] polyfills = SourceFiles(project, "*.cs")
            .Where(static f => string.Equals(Path.GetFileName(f), "Polyfills.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(polyfills);
    }

    [Theory]
    [MemberData(nameof(RuntimePackages))]
    public void RuntimePackage_SourceHasNoNetStandardBranches(string project)
    {
        string[] offenders = SourceFiles(project, "*.cs")
            .Where(static f => File.ReadAllText(f).Contains("NETSTANDARD2_0", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot(), f))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("SkathIO.Rogue.SourceGenerator")]
    [InlineData("SkathIO.Rogue.Validation.FluentValidation.SourceGenerator")]
    public void Generator_EmitsNoNetStandardBranches(string project)
    {
        // A directive inside a string literal is code the generator writes into the consumer's
        // compilation; every consumer is now net8.0 or later, so such a branch is dead.
        var emitted = new Regex("\"[^\"\\n]*#\\s*(if|elif)[^\"\\n]*NETSTANDARD2_0", RegexOptions.CultureInvariant);
        string[] offenders = SourceFiles(project, "*.cs")
            .Where(f => emitted.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepoRoot(), f))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void GeneratedSource_HasNoNetStandardBranches_AndKeepsModuleInitializer()
    {
        const string source = @"
using SkathIO.Rogue;
using System.Threading;
using System.Threading.Tasks;
public class Ping : ICommand<string> { }
public class PingHandler : ICommandHandler<Ping, string>
{
    public ValueTask<string> Handle(Ping request, CancellationToken ct) => ValueTask.FromResult(""pong"");
}
public class DeleteUser : ICommand { }
public class DeleteUserHandler : ICommandHandler<DeleteUser>
{
    public ValueTask Handle(DeleteUser request, CancellationToken ct) => ValueTask.CompletedTask;
}
public class Tick : IStreamQuery<int> { }
public class TickHandler : IStreamQueryHandler<Tick, int>
{
    public async System.Collections.Generic.IAsyncEnumerable<int> Handle(Tick request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return 1;
    }
}
public sealed class StreamLogBehavior<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : IStreamQuery<TResponse>
{
    public System.Collections.Generic.IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken ct) => next();
}";
        var result = GeneratorTestHelper.RunGeneratorAndAssertClean(source);
        var sources = result.Results.SelectMany(static r => r.GeneratedSources).ToList();
        Assert.NotEmpty(sources);

        foreach (var generated in sources)
        {
            Assert.DoesNotContain("NETSTANDARD2_0", generated.SourceText.ToString(), StringComparison.Ordinal);
        }

        string moduleInit = sources.First(static s => s.HintName == "RogueModuleInit.g.cs").SourceText.ToString();
        Assert.Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]", moduleInit);
        Assert.Contains("global::SkathIO.Rogue.RogueRegistrationBridge.Register(", moduleInit);
    }

    // ── Public API tracking ───────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(RuntimePackages))]
    public void RuntimePackage_HasNoPerTfmPublicApiFolders(string project)
    {
        string publicApiDir = Path.Combine(ProjectDir(project), "PublicAPI");
        Assert.False(Directory.Exists(Path.Combine(publicApiDir, "netstandard2.0")), "PublicAPI/netstandard2.0 is left");
        Assert.False(Directory.Exists(Path.Combine(publicApiDir, "modern")), "PublicAPI/modern is left");
    }

    [Theory]
    [MemberData(nameof(RuntimePackages))]
    public void RuntimePackage_StillTracksPublicApi_InOneShippedAndUnshippedPair(string project)
    {
        string[] shipped = SourceFiles(project, "PublicAPI.Shipped.txt");
        string[] unshipped = SourceFiles(project, "PublicAPI.Unshipped.txt");
        Assert.Single(shipped);
        Assert.Single(unshipped);
        Assert.Contains(File.ReadAllLines(shipped[0]), static l => l.Length > 0 && !l.StartsWith('#'));
    }

    // ── Package pins ──────────────────────────────────────────────────────────────────

    [Fact]
    public void FluentValidation_IsPinnedToMajor12()
    {
        string? version = PinnedVersion("FluentValidation");
        Assert.NotNull(version);
        Assert.StartsWith("12.", version, StringComparison.Ordinal);
    }

    [Fact]
    public void RoslynCompiler_StaysPinnedAt500()
    {
        Assert.Equal("5.0.0", PinnedVersion("Microsoft.CodeAnalysis.CSharp"));
    }

    [Theory]
    [MemberData(nameof(RuntimePackages))]
    public void RuntimePackage_PackageReferencesCarryNoVersion(string project)
    {
        XDocument doc = XDocument.Load(ProjectFile(project));
        string[] versioned = doc.Descendants("PackageReference")
            .Where(static e => e.Attribute("Version") is not null || e.Element("Version") is not null)
            .Select(static e => (string?)e.Attribute("Include") ?? "?")
            .ToArray();
        Assert.Empty(versioned);
    }

    [Theory]
    [MemberData(nameof(RuntimePackages))]
    public void RuntimePackage_ReferencesNoMediatRPackage(string project)
    {
        XDocument doc = XDocument.Load(ProjectFile(project));
        string[] banned = doc.Descendants("PackageReference")
            .Select(static e => (string?)e.Attribute("Include") ?? string.Empty)
            .Where(static id => id == "MediatR"
                || id.StartsWith("MediatR.", StringComparison.Ordinal)
                || id.StartsWith("Mediator.", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(banned);
    }

    [Fact]
    public void Src_NeverUsesBinaryFormatter()
    {
        string[] offenders = Directory.EnumerateDirectories(Path.Combine(RepoRoot(), "src"))
            .SelectMany(static d => SourceFilesIn(d, "*.cs"))
            .Where(static f => File.ReadAllText(f).Contains("BinaryFormatter", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot(), f))
            .ToArray();
        Assert.Empty(offenders);
    }

    // ── Build settings that keep the build at zero warnings ───────────────────────────

    [Fact]
    public void BuildProps_KeepZeroWarningAndStyleSettings()
    {
        XDocument doc = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Build.props"));
        Assert.Equal("true", LastValue(doc, "TreatWarningsAsErrors"));
        Assert.Equal("true", LastValue(doc, "EnforceCodeStyleInBuild"));
        Assert.Equal("enable", LastValue(doc, "Nullable"));
        Assert.Equal("disable", LastValue(doc, "ImplicitUsings"));
    }

    [Theory]
    [MemberData(nameof(RuntimePackages))]
    [MemberData(nameof(RoslynComponents))]
    public void Project_DoesNotRelaxZeroWarningSettings(string project)
    {
        XDocument doc = XDocument.Load(ProjectFile(project));
        Assert.DoesNotContain(doc.Descendants("TreatWarningsAsErrors"), static e => e.Value.Trim() == "false");
        Assert.DoesNotContain(doc.Descendants("EnforceCodeStyleInBuild"), static e => e.Value.Trim() == "false");
        Assert.DoesNotContain(doc.Descendants("Nullable"), static e => e.Value.Trim() != "enable");
        Assert.DoesNotContain(doc.Descendants("ImplicitUsings"), static e => e.Value.Trim() != "disable");
    }

    // ── Changelog ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Changelog_UnreleasedSection_RecordsTheNetStandardDrop()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoRoot(), "CHANGELOG.md"));
        int start = Array.FindIndex(lines, static l => l.StartsWith("## [Unreleased]", StringComparison.Ordinal));
        Assert.True(start >= 0, "CHANGELOG.md has no ## [Unreleased] section");
        int end = Array.FindIndex(lines, start + 1, static l => l.StartsWith("## ", StringComparison.Ordinal));
        IEnumerable<string> section = lines.Skip(start + 1).Take((end < 0 ? lines.Length : end) - start - 1);
        Assert.Contains(section, static l => l.Contains("netstandard2.0", StringComparison.OrdinalIgnoreCase));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

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

    private static string ProjectDir(string project) => Path.Combine(RepoRoot(), "src", project);

    private static string ProjectFile(string project)
    {
        string path = Path.Combine(ProjectDir(project), project + ".csproj");
        Assert.True(File.Exists(path), "missing project file " + path);
        return path;
    }

    private static string[] TargetFrameworks(string project)
    {
        XDocument doc = XDocument.Load(ProjectFile(project));
        return doc.Descendants()
            .Where(static e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .SelectMany(static e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] SourceFiles(string project, string pattern) => SourceFilesIn(ProjectDir(project), pattern);

    private static string[] SourceFilesIn(string dir, string pattern)
    {
        char sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
            .Where(f =>
            {
                string rel = Path.GetRelativePath(dir, f);
                return !rel.StartsWith("bin" + sep, StringComparison.Ordinal)
                    && !rel.StartsWith("obj" + sep, StringComparison.Ordinal);
            })
            .ToArray();
    }

    private static string? PinnedVersion(string id)
    {
        XDocument doc = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Packages.props"));
        return doc.Descendants("PackageVersion")
            .Where(e => (string?)e.Attribute("Include") == id)
            .Select(static e => (string?)e.Attribute("Version"))
            .SingleOrDefault();
    }

    private static string? LastValue(XDocument doc, string element) =>
        doc.Descendants(element).Select(static e => e.Value.Trim()).LastOrDefault();
}
