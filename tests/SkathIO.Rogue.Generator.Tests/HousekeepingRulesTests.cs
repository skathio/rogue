using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// Release housekeeping for 2.0.0. The allow-list keeps an entry that served only netstandard2.0
/// exactly when a packable project still restores that package, and drops its license note with it.
/// The packages that ship a Roslyn component ask that component for its output path without the
/// caller's global TargetFramework and RuntimeIdentifier, so a pack run with a global
/// -p:TargetFramework still finds and ships it. The site workflow's comment on its checkout pin names
/// the version that pin is.
/// </summary>
public sealed class HousekeepingRulesTests : IClassFixture<HousekeepingRulesTests.IsolatedBuilds>
{
    private readonly IsolatedBuilds _builds;

    public HousekeepingRulesTests(IsolatedBuilds builds) => _builds = builds;

    private const string AllowListPath = ".github/allowed-packages.txt";
    private const string SiteWorkflowPath = ".github/workflows/site.yml";
    private const string CiPath = ".github/workflows/ci.yml";

    private static readonly string[] _netStandardOnlyEntries =
    {
        "Microsoft.Bcl.AsyncInterfaces",
        "NETStandard.Library",
        "System.Buffers",
        "System.Diagnostics.DiagnosticSource",
    };

    // The packages the request names, with the Roslyn component each one packs.
    private static readonly (string Project, string Component)[] _componentPackages =
    {
        ("src/SkathIO.Rogue/SkathIO.Rogue.csproj", "SkathIO.Rogue.SourceGenerator"),
        ("src/SkathIO.Rogue.MediatR/SkathIO.Rogue.MediatR.csproj", "SkathIO.Rogue.Migration.Analyzer"),
        ("src/SkathIO.Rogue.Validation.FluentValidation/SkathIO.Rogue.Validation.FluentValidation.csproj", "SkathIO.Rogue.Validation.FluentValidation.SourceGenerator"),
    };

    private static readonly string[] _runtimeFrameworks = { "net8.0", "net10.0" };

    public static TheoryData<string> NetStandardOnlyEntries()
    {
        var data = new TheoryData<string>();
        foreach (string id in _netStandardOnlyEntries)
        {
            data.Add(id);
        }
        return data;
    }

    public static TheoryData<string> ComponentPackages()
    {
        var data = new TheoryData<string>();
        foreach ((string project, _) in _componentPackages)
        {
            data.Add(project);
        }
        return data;
    }

    public static TheoryData<string, string, string> ComponentPackagesByFramework()
    {
        var data = new TheoryData<string, string, string>();
        foreach ((string project, string component) in _componentPackages)
        {
            foreach (string framework in _runtimeFrameworks)
            {
                data.Add(project, component, framework);
            }
        }
        return data;
    }

    // ── Allow-list ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(NetStandardOnlyEntries))]
    public void AllowList_ListsANetStandardOnlyEntry_ExactlyWhenAPackableProjectRestoresIt(string id)
    {
        string[] restoredBy = RepoFiles.PackableProjects().Where(p => RestoreGraph(p).Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray();
        bool listed = AllowListIds().Contains(id, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            listed == (restoredBy.Length > 0),
            listed
                ? id + " is still allow-listed, but no packable project restores it."
                : id + " was removed from the allow-list, but these packable projects restore it: " + string.Join(", ", restoredBy));
    }

    [Theory]
    [MemberData(nameof(NetStandardOnlyEntries))]
    public void AllowList_KeepsNoLicenseNote_ForAnEntryItDropped(string id)
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoFiles.Root, AllowListPath));
        if (AllowListIds().Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Assert.DoesNotContain(lines, l => Regex.IsMatch(l, @"^\s*#\s*" + Regex.Escape(id) + @"\s+—", RegexOptions.IgnoreCase));
    }

    // ── Pack under a global TargetFramework ───────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ComponentPackages))]
    public void PackTarget_AsksForTheComponentPath_WithoutTheGlobalTargetFrameworkOrRuntimeIdentifier(string project)
    {
        XElement[] calls = XDocument.Load(Path.Combine(RepoFiles.Root, project)).Descendants("MSBuild")
            .Where(static e => (string?)e.Attribute("Targets") == "GetTargetPath")
            .ToArray();

        Assert.NotEmpty(calls);
        Assert.All(calls, static call =>
        {
            string[] removed = ((string?)call.Attribute("RemoveProperties") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Contains("TargetFramework", removed);
            Assert.Contains("RuntimeIdentifier", removed);
        });
    }

    [Theory]
    [MemberData(nameof(ComponentPackagesByFramework))]
    public void Pack_WithAGlobalTargetFramework_Succeeds_AndShipsTheComponent(string project, string component, string framework)
    {
        string output = Path.Combine(Path.GetTempPath(), "rogue-pack-" + Guid.NewGuid().ToString("N"));
        try
        {
            (int exitCode, string log) = DotnetPack(project, framework, _builds.ArtifactsOf(project), output);
            Assert.True(exitCode == 0, "dotnet pack " + project + " -p:TargetFramework=" + framework + " failed:\n" + log);

            string package = Assert.Single(Directory.GetFiles(output, "*.nupkg"));
            using ZipArchive archive = ZipFile.OpenRead(package);
            Assert.Contains(archive.Entries, e => e.FullName == "analyzers/dotnet/cs/" + component + ".dll");
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    // ── Site workflow ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SiteWorkflow_CheckoutComment_NamesTheVersionOfItsPin()
    {
        Dictionary<string, string> known = CheckoutPinVersions(RepoFiles.Read(CiPath));
        Dictionary<string, string> site = CheckoutPinVersions(RepoFiles.Read(SiteWorkflowPath));

        Assert.NotEmpty(site);
        Assert.All(site, pin =>
        {
            Assert.True(known.TryGetValue(pin.Key, out string? version), "ci.yml does not pin actions/checkout at " + pin.Key + ", so its version cannot be checked.");
            Assert.Equal(version, pin.Value);
        });
    }

    [Fact]
    public void SiteWorkflow_NoLongerNamesTheStaleCheckoutVersion()
    {
        Assert.DoesNotContain("v4.2.2", RepoFiles.Read(SiteWorkflowPath), StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    // Each actions/checkout SHA pin with the version its comment gives, on the line above or after it.
    private static Dictionary<string, string> CheckoutPinVersions(string workflow)
    {
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] lines = workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            Match pin = Regex.Match(lines[i], @"uses:\s*actions/checkout@(?<sha>[0-9a-f]{40})(\s*#\s*(?<version>v[\d.]+))?");
            if (!pin.Success)
            {
                continue;
            }
            Match above = i > 0 ? Regex.Match(lines[i - 1], @"^\s*#\s*actions/checkout\s+(?<version>v[\d.]+)") : Match.Empty;
            string version = pin.Groups["version"].Success ? pin.Groups["version"].Value : above.Groups["version"].Value;
            Assert.False(string.IsNullOrEmpty(version), "The actions/checkout pin " + pin.Groups["sha"].Value + " has no version comment.");
            pins[pin.Groups["sha"].Value] = version;
        }
        return pins;
    }

    private static (int ExitCode, string Log) DotnetPack(string project, string framework, string artifacts, string output) =>
        Dotnet("pack", project, "-c", BuildConfiguration(), "--no-build", "--artifacts-path", artifacts, "-p:TargetFramework=" + framework, "-o", output);

    private static (int ExitCode, string Log) Dotnet(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = RepoFiles.Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // No build servers: a reused MSBuild node would inherit the redirected pipes and keep them open.
        foreach (string argument in arguments.Concat(new[] { "--disable-build-servers", "-nologo" }))
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using Process process = Process.Start(start)!;
        System.Threading.Tasks.Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        bool exited = process.WaitForExit(TimeSpan.FromMinutes(5));
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
        }
        Assert.True(exited, "dotnet " + arguments[0] + " did not finish:\n" + stdout);
        return (process.ExitCode, stdout + stderr.Result);
    }

    // The configuration this test assembly was built in (bin/<Configuration>/<tfm>/).
    private static string BuildConfiguration()
    {
        string tfmDir = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('/', '\\'))!;
        return Path.GetFileName(tfmDir);
    }

    /// <summary>
    /// Each package built once, every target framework, into its own artifacts folder. A pack under a
    /// global TargetFramework still packs every framework's output, and the solution's own output may
    /// still be being written by a build running alongside these tests, so the pack reads this copy.
    /// </summary>
    public sealed class IsolatedBuilds : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rogue-pack-build-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string> _built = new(StringComparer.Ordinal);

        public string ArtifactsOf(string project)
        {
            lock (_built)
            {
                if (!_built.TryGetValue(project, out string? artifacts))
                {
                    artifacts = Path.Combine(_root, _built.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    (int exitCode, string log) = Dotnet("build", project, "-c", BuildConfiguration(), "--artifacts-path", artifacts);
                    Assert.True(exitCode == 0, "dotnet build " + project + " failed:\n" + log);
                    _built.Add(project, artifacts);
                }
                return artifacts;
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private static string[] AllowListIds() =>
        File.ReadLines(Path.Combine(RepoFiles.Root, AllowListPath))
            .Select(static l => l.Trim())
            .Where(static l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();

    // The package ids in a project's restore graph, every target framework: what
    // `dotnet list package --include-transitive` reports, read from the same restore output.
    private static string[] RestoreGraph(string project)
    {
        string assets = Path.Combine(RepoFiles.Root, Path.GetDirectoryName(project)!, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), assets + " is missing; restore the solution first.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));
        return document.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(static l => l.Value.GetProperty("type").GetString() == "package")
            .Select(static l => l.Name.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
