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
/// The jobs and validation benchmark results: a local run of every benchmark class in
/// bench/SkathIO.Rogue.Benchmarks.Jobs (Rogue against Hangfire.InMemory, and against Hangfire.SqlServer
/// in a container) and in bench/SkathIO.Rogue.Benchmarks.Validation is committed under
/// bench/results/&lt;date&gt;-&lt;sha&gt;/, newer than the 2026-07-13 results, in the report files the earlier
/// result directories hold. bench/RESULTS.md describes those results and the laptop hardware they ran on.
/// </summary>
public sealed class BenchResultsRulesTests
{
    private const string ResultsDirectory = "bench/results";
    private const string ResultsWriteUp = "bench/RESULTS.md";
    private const string JobsBench = "bench/SkathIO.Rogue.Benchmarks.Jobs";
    private const string ValidationBench = "bench/SkathIO.Rogue.Benchmarks.Validation";
    private const string GitHubReportSuffix = "-report-github.md";
    private static readonly DateTime _newestEarlierResults = new DateTime(2026, 7, 13);

    public static TheoryData<string> BenchProjects() => new TheoryData<string>(JobsBench, ValidationBench);

    public static TheoryData<string, string> BenchmarkClasses()
    {
        var data = new TheoryData<string, string>();
        foreach (string project in new[] { JobsBench, ValidationBench })
        {
            foreach (string benchmark in ConcreteBenchmarkClasses(project).Keys)
            {
                data.Add(project, benchmark);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(BenchProjects))]
    public void EachBenchProject_HasAtLeastOneBenchmarkClass_ToReport(string project)
    {
        Assert.NotEmpty(ConcreteBenchmarkClasses(project));
    }

    [Fact]
    public void TheJobsBench_MeasuresTheInMemoryAndTheSqlServerStore()
    {
        string[] classes = ConcreteBenchmarkClasses(JobsBench).Keys.ToArray();

        Assert.Contains(classes, static c => c.Contains("InMemory", StringComparison.Ordinal));
        Assert.Contains(classes, static c => c.Contains("SqlServer", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(BenchmarkClasses))]
    public void EachBenchmarkClass_HasAReport_InAResultDirectoryNewerThanTheEarlierResults(string project, string benchmark)
    {
        Assert.True(
            NewResultDirectoriesFor(benchmark).Length > 0,
            "No bench/results/<date>-<sha>/ directory dated after 2026-07-13 holds a " + benchmark + GitHubReportSuffix + " for " + project + ".");
    }

    [Theory]
    [MemberData(nameof(BenchmarkClasses))]
    public void EachNewReport_HasARow_ForEveryBenchmarkMethodOfItsClass(string project, string benchmark)
    {
        string[] methods = ConcreteBenchmarkClasses(project)[benchmark];
        string report = NewestReportText(benchmark);

        string[] missing = methods
            .Where(m => !Regex.IsMatch(report, MethodRow(Regex.Escape(m)), RegexOptions.Multiline))
            .ToArray();

        Assert.NotEmpty(methods);
        Assert.True(missing.Length == 0, "The newest " + benchmark + " report has no row for: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheNewJobsReports_PutRogueAgainstHangfire_OnEachStore()
    {
        foreach (string benchmark in ConcreteBenchmarkClasses(JobsBench).Keys)
        {
            string report = NewestReportText(benchmark);
            Assert.Matches(new Regex(MethodRow(@"Hangfire\w*"), RegexOptions.Multiline), report);
            Assert.Matches(new Regex(MethodRow(@"Rogue\w*"), RegexOptions.Multiline), report);
        }
    }

    [Fact]
    public void EveryNewResultDirectory_IsNamedByDateAndCommit()
    {
        string[] directories = NewResultDirectories();

        Assert.NotEmpty(directories);
        Assert.All(directories, static d => Assert.Matches(@"^\d{4}-\d{2}-\d{2}-[0-9a-f]{7,40}$", d));
    }

    [Fact]
    public void EveryNewReport_IsCommitted_NotLeftOutByGitignore()
    {
        string[] reports = NewReportPaths();
        HashSet<string> committable = new HashSet<string>(
            Git("ls-files", "--cached", "--others", "--exclude-standard", "--", ResultsDirectory)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);

        string[] ignored = reports.Where(r => !committable.Contains(r)).ToArray();

        Assert.NotEmpty(reports);
        Assert.True(ignored.Length == 0, "These result files are ignored by git, so they are not committed: " + string.Join(", ", ignored));
    }

    [Fact]
    public void TheWriteUp_PointsAtEachNewResultDirectory()
    {
        string writeUp = RepoFiles.Read(ResultsWriteUp);
        string[] directories = ConcreteBenchmarkClassNames().SelectMany(NewResultDirectoriesFor).Distinct(StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(directories);
        Assert.All(directories, d => Assert.Contains("bench/results/" + d, writeUp, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Hangfire.InMemory")]
    [InlineData("Hangfire.SqlServer")]
    public void TheWriteUp_NamesEachHangfireBaseline(string baseline)
    {
        Assert.Contains(baseline, RepoFiles.Read(ResultsWriteUp), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWriteUp_NotesTheLaptopHardware_TheNewResultsRanOn()
    {
        string writeUp = RepoFiles.Read(ResultsWriteUp);
        string[] cpus = ConcreteBenchmarkClassNames().Select(static b => ProcessorModel(NewestReportText(b))).Distinct(StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(cpus);
        Assert.All(cpus, cpu => Assert.Contains(cpu, writeUp, StringComparison.Ordinal));
        Assert.Contains("laptop", writeUp, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(BenchProjects))]
    public void EachBenchProject_KeepsPackageVersionsCentral(string project)
    {
        string csproj = RepoFiles.Read(project + "/" + Path.GetFileName(project) + ".csproj");
        string central = RepoFiles.Read("Directory.Packages.props");
        string[] packages = Regex.Matches(csproj, @"<PackageReference\s+Include=""(?<id>[^""]+)""")
            .Select(static m => m.Groups["id"].Value)
            .ToArray();

        Assert.DoesNotMatch(@"<PackageReference\s+Include=""[^""]+""[^>]*\bVersion\s*=", csproj);
        Assert.All(packages, id => Assert.Matches(@"<PackageVersion\s+Include=""" + Regex.Escape(id) + @"""", central));
    }

    // A table row whose Method cell matches the pattern. BenchmarkDotNet's GitHub exporter bolds the
    // first row of each parameter group ("| **Hangfire_EnqueueEach_AndRun** | **1000** |"), so the
    // cell may be wrapped in "**".
    private static string MethodRow(string methodPattern) =>
        @"^\|\s*(?:\*\*)?" + methodPattern + @"(?:\*\*)?\s*\|";

    // The processor model in a BenchmarkDotNet environment header, such as "i7-6700HQ".
    private static string ProcessorModel(string report)
    {
        Match line = Regex.Match(report, @"^(?<cpu>(Intel|AMD|Apple)\b[^\n]*)$", RegexOptions.Multiline);
        Assert.True(line.Success, "The report names no processor in its environment header.");
        string name = Regex.Split(line.Groups["cpu"].Value, @"\s(CPU|@|\()")[0].Trim();
        return name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
    }

    private static string[] ConcreteBenchmarkClassNames() =>
        new[] { JobsBench, ValidationBench }.SelectMany(static p => ConcreteBenchmarkClasses(p).Keys).ToArray();

    // Each non-abstract benchmark class of a bench project, with its [Benchmark] methods and those it inherits.
    private static Dictionary<string, string[]> ConcreteBenchmarkClasses(string project)
    {
        Dictionary<string, DeclaredClass> declared = DeclaredClasses(project);
        return declared
            .Where(static c => !c.Value.IsAbstract)
            .Select(c => (Name: c.Key, Methods: InheritedMethods(c.Value, declared)))
            .Where(static c => c.Methods.Length > 0)
            .ToDictionary(static c => c.Name, static c => c.Methods, StringComparer.Ordinal);
    }

    private static string[] InheritedMethods(DeclaredClass declaredClass, Dictionary<string, DeclaredClass> declared)
    {
        var all = new List<string>(declaredClass.Methods);
        string? current = declaredClass.Base;
        while (current is not null && declared.TryGetValue(current, out DeclaredClass? parent))
        {
            all.AddRange(parent.Methods);
            current = parent.Base;
        }
        return all.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, DeclaredClass> DeclaredClasses(string project)
    {
        var declared = new Dictionary<string, DeclaredClass>(StringComparer.Ordinal);
        string dir = Path.Combine(RepoFiles.Root, project);
        foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).Where(static f => !IsBuildOutput(f)))
        {
            string text = File.ReadAllText(file);
            MatchCollection classes = Regex.Matches(text, @"\b(?<abstract>abstract\s+)?(?:sealed\s+)?(?:partial\s+)?class\s+(?<name>\w+)(?:\s*:\s*(?<base>\w+))?");
            for (int i = 0; i < classes.Count; i++)
            {
                int end = i + 1 < classes.Count ? classes[i + 1].Index : text.Length;
                declared[classes[i].Groups["name"].Value] = Declare(classes[i], text.Substring(classes[i].Index, end - classes[i].Index));
            }
        }
        return declared;
    }

    private static DeclaredClass Declare(Match declaration, string body)
    {
        string[] methods = Regex.Matches(body, @"\[Benchmark\b[^\]]*\]\s*public\s+[^(=]*?\b(?<name>\w+)\s*\(")
            .Select(static m => m.Groups["name"].Value)
            .ToArray();
        string? baseName = declaration.Groups["base"].Success ? declaration.Groups["base"].Value : null;
        return new DeclaredClass(declaration.Groups["abstract"].Success, baseName, methods);
    }

    // Result directories dated after the newest earlier results, by name ("2026-11-21-abc1234").
    private static string[] NewResultDirectories() =>
        Directory.GetDirectories(Path.Combine(RepoFiles.Root, ResultsDirectory))
            .Select(static d => Path.GetFileName(d))
            .Where(static d => ResultDate(d) is DateTime date && date > _newestEarlierResults)
            .OrderBy(static d => d, StringComparer.Ordinal)
            .ToArray();

    private static DateTime? ResultDate(string directory) =>
        directory.Length >= 10 && DateTime.TryParseExact(directory.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
            ? date
            : null;

    private static string[] NewResultDirectoriesFor(string benchmark) =>
        NewResultDirectories()
            .Where(d => ReportFile(d, benchmark) is not null)
            .ToArray();

    // The GitHub report of a benchmark class in a result directory; BenchmarkDotNet names it "<Namespace>.<Class>-report-github.md".
    private static string? ReportFile(string directory, string benchmark) =>
        Directory.GetFiles(Path.Combine(RepoFiles.Root, ResultsDirectory, directory), "*" + GitHubReportSuffix, SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).EndsWith("." + benchmark + GitHubReportSuffix, StringComparison.Ordinal));

    private static string NewestReportText(string benchmark)
    {
        string[] directories = NewResultDirectoriesFor(benchmark);
        Assert.True(directories.Length > 0, "No new result directory holds a report for " + benchmark + ".");
        return File.ReadAllText(ReportFile(directories[^1], benchmark)!);
    }

    private static string[] NewReportPaths() =>
        NewResultDirectories()
            .SelectMany(static d => Directory.GetFiles(Path.Combine(RepoFiles.Root, ResultsDirectory, d), "*", SearchOption.AllDirectories))
            .Select(static f => Path.GetRelativePath(RepoFiles.Root, f).Replace('\\', '/'))
            .ToArray();

    private static bool IsBuildOutput(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal) || normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string Git(params string[] arguments)
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
        Assert.True(git.ExitCode == 0, "git " + string.Join(" ", arguments) + " failed: " + error);
        return output;
    }

    private sealed record DeclaredClass(bool IsAbstract, string? Base, string[] Methods);
}
