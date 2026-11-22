using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The 2.0.0 benchmark baseline. The full core suite (every benchmark class in
/// bench/SkathIO.Rogue.Benchmarks, run with --filter '*' and no --job short) ran on this machine, and so
/// did the validation suite, because its 2026-11-21 run was a ShortRun. The reports are committed under
/// bench/results/&lt;YYYY-MM-DD&gt;-&lt;short-sha&gt;/ with a matching !bench/results/&lt;dir&gt;/ line in .gitignore.
/// bench/RESULTS.md and docs/benchmarks.md point at the new results; docs/benchmarks.md adds the jobs
/// results against Hangfire and no longer calls the validation results uncommitted; one of the two notes
/// the laptop hardware (an i7-6700HQ, with Docker on a spinning disk).
/// </summary>
public sealed class BenchFullRunRulesTests
{
    private const string ResultsDirectory = "bench/results";
    private const string ResultsWriteUp = "bench/RESULTS.md";
    private const string BenchmarksDoc = "docs/benchmarks.md";
    private const string CoreBench = "bench/SkathIO.Rogue.Benchmarks";
    private const string ValidationBench = "bench/SkathIO.Rogue.Benchmarks.Validation";
    private const string JobsBench = "bench/SkathIO.Rogue.Benchmarks.Jobs";
    private const string EarlierResults = "2026-11-21-284630b";
    private const string GitHubReportSuffix = "-report-github.md";
    private static readonly DateTime _earlierResultsDate = new DateTime(2026, 11, 21);

    public static TheoryData<string> CoreBenchmarkClasses() => ClassesOf(CoreBench);

    public static TheoryData<string> ValidationBenchmarkClasses() => ClassesOf(ValidationBench);

    // ── The new result directory ──────────────────────────────────────────────────────

    [Fact]
    public void ANewResultDirectory_IsNewerThanTheEarlierResults()
    {
        Assert.NotEmpty(NewResultDirectories());
    }

    [Fact]
    public void EveryNewResultDirectory_IsNamedByDateAndShortSha()
    {
        Assert.All(NewResultDirectories(), static d => Assert.Matches(@"^\d{4}-\d{2}-\d{2}-[0-9a-f]{7,40}$", d));
    }

    [Fact]
    public void EveryNewResultDirectory_NamesACommitOfThisRepository()
    {
        string[] directories = NewResultDirectories();

        Assert.NotEmpty(directories);
        Assert.All(directories, static d => Assert.Equal(0, GitExitCode("cat-file", "-e", d.Substring(11) + "^{commit}")));
    }

    [Fact]
    public void EveryNewResultDirectory_HasItsOwnUnignoreLine_InGitignore()
    {
        string[] lines = RepoFiles.Read(".gitignore").Split('\n').Select(static l => l.Trim()).ToArray();
        string[] directories = NewResultDirectories();

        Assert.NotEmpty(directories);
        Assert.All(directories, d => Assert.Contains("!" + ResultsDirectory + "/" + d + "/", lines));
    }

    [Fact]
    public void EveryFileOfTheNewResults_IsCommitted()
    {
        HashSet<string> committed = new HashSet<string>(
            Git("ls-tree", "-r", "--name-only", "HEAD", "--", ResultsDirectory).Split('\n', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);
        string[] files = NewResultDirectories()
            .SelectMany(static d => Directory.GetFiles(Path.Combine(RepoFiles.Root, ResultsDirectory, d), "*", SearchOption.AllDirectories))
            .Select(static f => Path.GetRelativePath(RepoFiles.Root, f).Replace('\\', '/'))
            .ToArray();

        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.Contains(f, committed));
    }

    [Theory]
    [InlineData(ResultsWriteUp)]
    [InlineData(BenchmarksDoc)]
    [InlineData(".gitignore")]
    public void TheWriteUpAndGitignore_HaveNoUncommittedChange(string path)
    {
        Assert.Equal(0, GitExitCode("diff", "--quiet", "HEAD", "--", path));
    }

    // ── The full core suite ───────────────────────────────────────────────────────────

    [Fact]
    public void TheCoreSuite_HasBenchmarkClassesToRun()
    {
        Assert.NotEmpty(BenchmarkClasses(CoreBench));
    }

    [Theory]
    [MemberData(nameof(CoreBenchmarkClasses))]
    public void EachCoreBenchmarkClass_HasAFullRunReport_WithARowPerMethod(string benchmark)
    {
        AssertFullRunReport(CoreBench, benchmark);
    }

    // ── The validation suite, rerun when its 2026-11-21 run was short ─────────────────

    [Theory]
    [MemberData(nameof(ValidationBenchmarkClasses))]
    public void EachValidationBenchmarkClass_WhoseEarlierRunWasShort_HasAFullRunReport(string benchmark)
    {
        string? earlier = ReportText(EarlierResults, benchmark);
        Assert.NotNull(earlier);
        if (!IsShortRun(earlier))
        {
            return;
        }

        AssertFullRunReport(ValidationBench, benchmark);
    }

    // ── The write-up ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ResultsWriteUp)]
    [InlineData(BenchmarksDoc)]
    public void TheWriteUp_PointsAtEachNewResultDirectory_ThatHoldsTheFullRuns(string path)
    {
        string text = RepoFiles.Read(path);
        string[] directories = BenchmarkClasses(CoreBench).Keys
            .Concat(BenchmarkClasses(ValidationBench).Keys)
            .Select(static b => NewestFullRunDirectory(b))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(directories);
        Assert.All(directories, d => Assert.Contains(ResultsDirectory + "/" + d, text, StringComparison.Ordinal));
    }

    [Fact]
    public void TheBenchmarksDoc_HasTheJobsResultsAgainstHangfire_FromACommittedResultDirectory()
    {
        string doc = RepoFiles.Read(BenchmarksDoc);
        string[] jobsDirectories = BenchmarkClasses(JobsBench).Keys.SelectMany(ResultDirectoriesWithReport).Distinct(StringComparer.Ordinal).ToArray();

        Assert.Contains("Hangfire", doc, StringComparison.Ordinal);
        Assert.NotEmpty(jobsDirectories);
        Assert.Contains(jobsDirectories, d => doc.Contains(ResultsDirectory + "/" + d, StringComparison.Ordinal));
    }

    [Fact]
    public void TheBenchmarksDoc_NoLongerCallsTheValidationResultsUncommitted()
    {
        Assert.DoesNotMatch(new Regex(@"not\s+yet\s+a\s+committed", RegexOptions.IgnoreCase), RepoFiles.Read(BenchmarksDoc));
    }

    [Fact]
    public void TheBenchmarksDoc_PointsAtACommittedResultDirectory_ForTheValidationEngines()
    {
        string doc = RepoFiles.Read(BenchmarksDoc);
        string[] directories = ResultDirectoriesWithReport("InHouseValidationBenchmarks");

        Assert.NotEmpty(directories);
        Assert.Contains(directories, d => doc.Contains(ResultsDirectory + "/" + d, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("i7-6700HQ")]
    [InlineData(@"spinning\s+(hard\s+)?(disk|drive)")]
    [InlineData("Docker")]
    public void TheWriteUp_NotesTheLaptopHardware(string pattern)
    {
        string text = RepoFiles.Read(ResultsWriteUp) + "\n" + RepoFiles.Read(BenchmarksDoc);

        Assert.Matches(new Regex(pattern, RegexOptions.IgnoreCase), text);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    private static void AssertFullRunReport(string project, string benchmark)
    {
        string? directory = NewestFullRunDirectory(benchmark);
        Assert.True(directory is not null, "No result directory dated after 2026-11-21 holds a full (non-ShortRun) " + benchmark + GitHubReportSuffix + ".");

        string report = ReportText(directory!, benchmark)!;
        string[] missing = BenchmarkClasses(project)[benchmark]
            .Where(m => !Regex.IsMatch(report, @"^\|\s*(?:\*\*)?" + Regex.Escape(m) + @"(?:\*\*)?\s*\|", RegexOptions.Multiline))
            .ToArray();
        Assert.True(missing.Length == 0, "The full " + benchmark + " report in " + directory + " has no row for: " + string.Join(", ", missing));
    }

    private static bool IsShortRun(string report) => report.Contains("ShortRun", StringComparison.Ordinal);

    private static string? NewestFullRunDirectory(string benchmark) =>
        NewResultDirectories().LastOrDefault(d => ReportText(d, benchmark) is string report && !IsShortRun(report));

    private static TheoryData<string> ClassesOf(string project)
    {
        var data = new TheoryData<string>();
        foreach (string benchmark in BenchmarkClasses(project).Keys)
        {
            data.Add(benchmark);
        }
        return data;
    }

    // Each benchmark class of a bench project with its [Benchmark] methods. Each file holds at most one
    // benchmark class, so the methods of a file belong to the class in it that declares them.
    private static Dictionary<string, string[]> BenchmarkClasses(string project)
    {
        var classes = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(Path.Combine(RepoFiles.Root, project), "*.cs", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (normalized.Contains("/bin/", StringComparison.Ordinal) || normalized.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }
            string text = File.ReadAllText(file);
            string[] methods = Regex.Matches(text, @"\[Benchmark\b[^\]]*\]\s*public\s+[^(=]*?\b(?<name>\w+)\s*\(")
                .Select(static m => m.Groups["name"].Value)
                .ToArray();
            Match declaration = Regex.Match(text, @"public\s+(?:sealed\s+)?class\s+(?<name>\w+Benchmarks)\b");
            if (methods.Length > 0 && declaration.Success)
            {
                classes[declaration.Groups["name"].Value] = methods;
            }
        }
        return classes;
    }

    // Result directories dated after the earlier 2026-11-21 results, oldest first.
    private static string[] NewResultDirectories() =>
        Directory.GetDirectories(Path.Combine(RepoFiles.Root, ResultsDirectory))
            .Select(static d => Path.GetFileName(d))
            .Where(static d => d.Length >= 10
                && DateTime.TryParseExact(d.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
                && date > _earlierResultsDate)
            .OrderBy(static d => d, StringComparer.Ordinal)
            .ToArray();

    private static string[] ResultDirectoriesWithReport(string benchmark) =>
        Directory.GetDirectories(Path.Combine(RepoFiles.Root, ResultsDirectory))
            .Select(static d => Path.GetFileName(d))
            .Where(d => ReportText(d, benchmark) is not null)
            .ToArray();

    // BenchmarkDotNet names a class's GitHub report "<Namespace>.<Class>-report-github.md".
    private static string? ReportText(string directory, string benchmark)
    {
        string? file = Directory.GetFiles(Path.Combine(RepoFiles.Root, ResultsDirectory, directory), "*" + GitHubReportSuffix, SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).EndsWith("." + benchmark + GitHubReportSuffix, StringComparison.Ordinal));
        return file is null ? null : File.ReadAllText(file);
    }

    private static string Git(params string[] arguments)
    {
        (int exitCode, string output, string error) = RunGit(arguments);
        Assert.True(exitCode == 0, "git " + string.Join(" ", arguments) + " failed: " + error);
        return output;
    }

    private static int GitExitCode(params string[] arguments) => RunGit(arguments).ExitCode;

    private static (int ExitCode, string Output, string Error) RunGit(string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = RepoFiles.Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process git = Process.Start(start)!;
        string output = git.StandardOutput.ReadToEnd();
        string error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        return (git.ExitCode, output, error);
    }
}
