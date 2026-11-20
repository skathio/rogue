using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The jobs benchmark: a bench project measures 1k, 10k and 100k small jobs on Rogue against
/// Hangfire.InMemory (the per-job overhead, run as a CI smoke) and against Hangfire.SqlServer on the same
/// database as Rogue's SQL Server store. Both Hangfire packages are bench only and pinned centrally. The
/// CI smoke gates its result against a ceiling in .bench/thresholds.json, and the existing NoBehavior
/// smoke and its committed contract stay as they are.
/// </summary>
public sealed class JobsBenchRulesTests
{
    private const string HangfireInMemory = "Hangfire.InMemory";
    private const string HangfireSqlServer = "Hangfire.SqlServer";
    private const string ThresholdsPath = ".bench/thresholds.json";
    private static readonly string[] _existingThresholdKeys = { "scenario1_rogue_allocated_bytes", "scenario1_rogue_ns_p95", "interface_path_ceiling_bytes" };

    [Fact]
    public void JobsBench_IsAnExecutableBenchProject_ThatShipsNothing()
    {
        string project = RepoFiles.Read(JobsBenchProject());

        Assert.Matches(@"<OutputType>\s*Exe\s*</OutputType>", project);
        Assert.DoesNotMatch(@"<IsPackable>\s*true\s*</IsPackable>", project);
    }

    [Theory]
    [InlineData(HangfireInMemory)]
    [InlineData(HangfireSqlServer)]
    public void JobsBench_ReferencesTheHangfireBaseline_WithItsVersionPinnedCentrally(string package)
    {
        string project = RepoFiles.Read(JobsBenchProject());

        Assert.Matches(@"<PackageReference\s+Include=""" + Regex.Escape(package) + @"""", project);
        Assert.DoesNotMatch(@"<PackageReference\s+Include=""[^""]+""[^>]*\bVersion\s*=", project);
        Assert.Matches(@"<PackageVersion\s+Include=""" + Regex.Escape(package) + @"""\s+Version=""[^""]+""", RepoFiles.Read("Directory.Packages.props"));
    }

    [Fact]
    public void Hangfire_IsReferencedOnlyFromBench()
    {
        string[] outside = AllProjects()
            .Where(static p => !p.StartsWith("bench/", StringComparison.Ordinal))
            .Where(static p => Regex.IsMatch(RepoFiles.Read(p), @"<PackageReference\s+Include=""Hangfire[^""]*"""))
            .ToArray();

        Assert.True(outside.Length == 0, "Hangfire is bench only, yet referenced by: " + string.Join(", ", outside));
    }

    [Fact]
    public void Hangfire_IsInNoPackableProjectsRestoreGraph()
    {
        string[] packable = RepoFiles.PackableProjects();

        string[] leaks = packable
            .SelectMany(p => RestoreGraph(p).Where(IsHangfire).Select(id => p + ": " + id))
            .ToArray();

        Assert.NotEmpty(packable);
        Assert.True(leaks.Length == 0, "Hangfire is bench only, yet ships with a package: " + string.Join(", ", leaks));
    }

    [Fact]
    public void JobsBench_RunsRogueJobs_OnTheSqlServerStore()
    {
        string project = RepoFiles.Read(JobsBenchProject());

        Assert.Matches(@"<ProjectReference\s+Include=""[^""]*SkathIO\.Rogue\.Jobs\.csproj""", project);
        Assert.Matches(@"<ProjectReference\s+Include=""[^""]*SkathIO\.Rogue\.Jobs\.SqlServer\.csproj""", project);
        Assert.Matches(@"\bAddRogueSqlServerJobs\b|\bSqlServerJobStore\b", BenchSourceText());
    }

    [Theory]
    [InlineData(@"\bUseInMemoryStorage\b|\bnew\s+InMemoryStorage\b", HangfireInMemory)]
    [InlineData(@"\bUseSqlServerStorage\b|\bnew\s+SqlServerStorage\b", HangfireSqlServer)]
    public void JobsBench_MeasuresAgainstTheHangfireBaseline(string pattern, string baseline)
    {
        Assert.True(Regex.IsMatch(BenchSourceText(), pattern), "No benchmark source sets up " + baseline + " storage.");
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void JobsBench_RunsAtEachJobCountTheRequestNames(int jobs)
    {
        long[] literals = Regex.Matches(BenchSourceText(), @"(?<![\w.])\d[\d_]*(?![\w.])")
            .Select(static m => long.TryParse(m.Value.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : -1)
            .ToArray();

        Assert.Contains((long)jobs, literals);
    }

    [Fact]
    public void Ci_RunsTheJobsBenchSmoke_AndGatesItAgainstACeilingInTheThresholds()
    {
        string job = JobsBenchCiJob();
        Dictionary<string, JsonElement> thresholds = Thresholds();

        string[] ceilings = Regex.Matches(job, @"\.(?<key>[A-Za-z_][A-Za-z0-9_]*)\b")
            .Select(static m => m.Groups["key"].Value)
            .Where(key => thresholds.ContainsKey(key) && !_existingThresholdKeys.Contains(key, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(ThresholdsPath, job, StringComparison.Ordinal);
        Assert.True(ceilings.Length > 0, "The CI job that runs the jobs bench reads no new ceiling from " + ThresholdsPath + ".");
        Assert.All(ceilings, key => Assert.True(
            thresholds[key].ValueKind == JsonValueKind.Number && thresholds[key].GetDouble() > 0,
            key + " in " + ThresholdsPath + " is not a positive number."));
    }

    [Fact]
    public void TheNoBehaviorSmoke_AndItsCommittedContract_StayAsTheyAre()
    {
        Dictionary<string, JsonElement> thresholds = Thresholds();
        string ci = RepoFiles.Read(".github/workflows/ci.yml");

        Assert.Equal(0, thresholds["scenario1_rogue_allocated_bytes"].GetInt32());
        Assert.True(thresholds.ContainsKey("interface_path_ceiling_bytes"), "interface_path_ceiling_bytes is gone from " + ThresholdsPath + ".");
        Assert.Contains("bench/SkathIO.Rogue.Benchmarks/SkathIO.Rogue.Benchmarks.csproj -c Release -- --filter '*NoBehavior*'", ci, StringComparison.Ordinal);
    }

    private static bool IsHangfire(string id) => id.StartsWith("Hangfire", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildOutput(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal) || normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string Relative(string path) => Path.GetRelativePath(RepoFiles.Root, path).Replace('\\', '/');

    private static string[] AllProjects() =>
        new[] { "src", "tests", "samples", "bench" }
            .Select(static d => Path.Combine(RepoFiles.Root, d))
            .Where(Directory.Exists)
            .SelectMany(static d => Directory.GetFiles(d, "*.csproj", SearchOption.AllDirectories))
            .Where(static p => !IsBuildOutput(p))
            .Select(Relative)
            .ToArray();

    // The bench project that measures Rogue's jobs against Hangfire.InMemory, as "bench/X/X.csproj".
    private static string JobsBenchProject()
    {
        string[] projects = AllProjects()
            .Where(static p => p.StartsWith("bench/", StringComparison.Ordinal))
            .Where(static p => Regex.IsMatch(RepoFiles.Read(p), @"<PackageReference\s+Include=""Hangfire\.InMemory"""))
            .ToArray();

        Assert.True(projects.Length == 1, "Expected one bench project that references " + HangfireInMemory + ", found " + projects.Length + ".");
        return projects[0];
    }

    private static string BenchSourceText()
    {
        string dir = Path.Combine(RepoFiles.Root, Path.GetDirectoryName(JobsBenchProject())!);
        string[] sources = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).Where(static f => !IsBuildOutput(f)).ToArray();

        Assert.NotEmpty(sources);
        return string.Join("\n", sources.Select(File.ReadAllText));
    }

    // The ci.yml job whose steps run the jobs bench project.
    private static string JobsBenchCiJob()
    {
        string project = JobsBenchProject();
        string ci = RepoFiles.Read(".github/workflows/ci.yml");
        string[] jobs = Regex.Split(ci, @"^(?=  [A-Za-z0-9_-]+:\s*$)", RegexOptions.Multiline);

        string[] running = jobs
            .Where(j => Regex.IsMatch(j, @"dotnet\s+run\s+--project\s+""?" + Regex.Escape(project)))
            .ToArray();

        Assert.True(running.Length > 0, "No CI job runs " + project + ".");
        return string.Join("\n", running);
    }

    private static Dictionary<string, JsonElement> Thresholds()
    {
        using JsonDocument document = JsonDocument.Parse(RepoFiles.Read(ThresholdsPath));
        return document.RootElement.EnumerateObject().ToDictionary(static p => p.Name, static p => p.Value.Clone(), StringComparer.Ordinal);
    }

    // The package ids in a project's restore graph, every target framework: what the license check lists.
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
