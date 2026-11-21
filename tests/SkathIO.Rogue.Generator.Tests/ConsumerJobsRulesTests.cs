using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The jobs scenarios of the end-to-end consumer suite: through the packed packages, the consumer
/// solution enqueues, delays, repeats, deduplicates, retries and dead-letters jobs on the in-memory,
/// Postgres and SQL Server stores. The tests are deterministic, and those that need a database
/// container skip on a machine without Docker but run, and fail without it, in CI.
/// </summary>
public sealed class ConsumerJobsRulesTests
{
    private const string ConsumerDirectory = "tests/consumer";
    private const string ConsumerSolution = ConsumerDirectory + "/SkathIO.Rogue.Consumer.sln";
    private const string RunScript = ConsumerDirectory + "/run.sh";
    private const string JobsPackage = "SkathIO.Rogue.Jobs";

    private static readonly string[] _skippedDirectories = { "bin", "obj", "TestResults" };

    public static TheoryData<string, string> StoresAndScenarios()
    {
        var data = new TheoryData<string, string>();
        foreach (string store in Stores.Keys)
        {
            foreach (string scenario in Scenarios.Keys)
            {
                data.Add(store, scenario);
            }
        }
        return data;
    }

    public static TheoryData<string> AllStores() => new TheoryData<string>(Stores.Keys);

    public static TheoryData<string> ContainerStores() => new TheoryData<string>("Postgres", "SQL Server");

    // How a consumer test file sets up each store.
    private static IReadOnlyDictionary<string, string> Stores { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["in-memory"] = @"\bInMemoryJobStore\b|\bAddRogueJobs\s*\(",
        ["Postgres"] = @"\bAddRoguePostgresJobs\s*\(|\bnew\s+PostgresJobStore\s*\(",
        ["SQL Server"] = @"\bAddRogueSqlServerJobs\s*\(|\bnew\s+SqlServerJobStore\s*\(",
    };

    // The package each store comes from, restored from the local feed.
    private static IReadOnlyDictionary<string, string> StorePackages { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["in-memory"] = JobsPackage,
        ["Postgres"] = JobsPackage + ".Postgres",
        ["SQL Server"] = JobsPackage + ".SqlServer",
    };

    // The public API each scenario goes through.
    private static IReadOnlyDictionary<string, string> Scenarios { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["enqueue"] = @"\.Enqueue(Many)?\s*(<[^<>()]*>)?\s*\(",
        ["delayed"] = @"\.Schedule\s*(<[^<>()]*>)?\s*\(",
        ["recurring"] = @"\bAddRecurringJob\s*\(|\[\s*(SkathIO\.Rogue\.Jobs\.)?Recurring(Attribute)?\s*\(",
        ["unique"] = @"\bUniqueKey\b",
        ["retry"] = @"(?i)\b\w*(retr(y|ie)|attempt)\w*\b",
        ["dead-letter"] = @"\bJobState\.DeadLettered\b",
        ["a worker that runs the jobs"] = @"\.RunOnceAsync\s*\(|\.RunAsync\s*\(|\bAddRogueJobWorker\s*\(",
    };

    [Theory]
    [MemberData(nameof(StoresAndScenarios))]
    public void ConsumerTests_RunEachJobsScenario_OnEachStore(string store, string scenario)
    {
        string covered = StoreText(store);

        Assert.True(Regex.IsMatch(covered, Scenarios[scenario]), "The consumer tests on the " + store + " store do not cover " + scenario + ".");
    }

    [Theory]
    [MemberData(nameof(AllStores))]
    public void ConsumerProjects_RestoreEachStore_ByPackageReference(string store)
    {
        string[] files = StoreUnits(store).Select(static u => u.File).Distinct(StringComparer.Ordinal).ToArray();
        string[] missing = files.Select(ProjectOf).Distinct(StringComparer.Ordinal)
            .Where(p => !PackageReferences(p).Any(id => IsStorePackage(store, id)))
            .ToArray();

        Assert.NotEmpty(files);
        Assert.True(missing.Length == 0, "These consumer projects run the " + store + " store without a package reference to it: " + string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(ContainerStores))]
    public void ContainerTests_SkipWithoutDocker_ButNotInCi(string store)
    {
        string covered = StoreText(store);

        Assert.Matches(@"GetEnvironmentVariable\s*\(\s*""(CI|GITHUB_ACTIONS)""\s*\)", covered);
        Assert.Matches(@"\bSkip\b", covered);
        Assert.Matches(@"(?i)docker", covered);
    }

    [Fact]
    public void JobsConsumerTests_ReadNoWallClock_AndDoNotSleep()
    {
        string[] jobsFiles = ConsumerSourceFiles().Where(static f => Regex.IsMatch(File.ReadAllText(f), @"(?m)^\s*using\s+SkathIO\.Rogue\.Jobs(\.\w+)*\s*;")).ToArray();
        string[] clocked = jobsFiles
            .Where(static f => Regex.IsMatch(File.ReadAllText(f), @"\bThread\.Sleep\s*\(|\bStopwatch\b|\bDateTime(Offset)?\.(Utc)?Now\b"))
            .Select(static f => Path.GetRelativePath(RepoFiles.Root, f))
            .ToArray();

        Assert.NotEmpty(jobsFiles);
        Assert.True(clocked.Length == 0, "These jobs tests read the wall clock or sleep, so they are not deterministic: " + string.Join(", ", clocked));
    }

    [Fact]
    public void Ci_RunsTheContainerTests_OnARunnerWithDocker()
    {
        string job = ConsumerJob();

        Assert.Matches(@"(?m)^\s+runs-on:\s*ubuntu-", job);
        Assert.DoesNotMatch(@"(?m)^\s+CI:\s*['""]?false", job);
        Assert.DoesNotMatch(@"--filter\b|\bSkipDocker|Category\s*!=", job + "\n" + RepoFiles.Read(RunScript));
    }

    private static bool IsStorePackage(string store, string id) =>
        store == "in-memory"
            ? id.Equals(JobsPackage, StringComparison.OrdinalIgnoreCase) || id.StartsWith(JobsPackage + ".", StringComparison.OrdinalIgnoreCase)
            : id.Equals(StorePackages[store], StringComparison.OrdinalIgnoreCase);

    // The ci.yml job that runs the consumer suite.
    private static string ConsumerJob()
    {
        string ci = RepoFiles.Read(".github/workflows/ci.yml");
        string[] jobs = Regex.Split(ci, @"(?m)^(?=  [A-Za-z][\w-]*:\s*$)").Where(static j => j.Contains(RunScript, StringComparison.Ordinal)).ToArray();
        Assert.True(jobs.Length == 1, "Expected one ci.yml job that runs " + RunScript + ".");
        return jobs[0];
    }

    // The consumer solution's projects, as full paths.
    private static string[] ConsumerProjects()
    {
        string directory = Path.Combine(RepoFiles.Root, ConsumerDirectory);
        return Regex.Matches(RepoFiles.Read(ConsumerSolution), @"""(?<path>[^""]+\.csproj)""")
            .Select(m => Path.GetFullPath(Path.Combine(directory, m.Groups["path"].Value.Replace('\\', '/'))))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> SourcesUnder(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            yield return file;
        }
        foreach (string sub in Directory.EnumerateDirectories(directory).Where(static d => !_skippedDirectories.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)))
        {
            foreach (string file in SourcesUnder(sub))
            {
                yield return file;
            }
        }
    }

    private static string[] ConsumerSourceFiles() =>
        ConsumerProjects().Select(static p => Path.GetDirectoryName(p)!).Where(Directory.Exists).SelectMany(SourcesUnder).Distinct(StringComparer.Ordinal).ToArray();

    private static string ProjectOf(string file) =>
        ConsumerProjects().Where(p => file.StartsWith(Path.GetDirectoryName(p)! + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderByDescending(static p => p.Length)
            .First();

    // The package ids a consumer project restores directly, through the consumer projects it references,
    // or through the build files beside the consumer solution.
    private static string[] PackageReferences(string project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(new[] { project });
        var text = new List<string>(Directory.GetFiles(Path.Combine(RepoFiles.Root, ConsumerDirectory), "Directory.Build.*").Select(File.ReadAllText));
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            if (!seen.Add(current) || !File.Exists(current))
            {
                continue;
            }
            string csproj = File.ReadAllText(current);
            text.Add(csproj);
            foreach (Match reference in Regex.Matches(csproj, @"<ProjectReference\s+Include=""(?<path>[^""]+)"""))
            {
                pending.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, reference.Groups["path"].Value.Replace('\\', '/'))));
            }
        }
        return text.SelectMany(static t => Regex.Matches(t, @"<PackageReference\s+Include=""(?<id>[^""]+)""").Select(static m => m.Groups["id"].Value)).ToArray();
    }

    // Each type declaration in the consumer sources, with its attributes, up to the next declaration.
    private static SourceUnit[] Units() =>
        ConsumerSourceFiles()
            .SelectMany(static f => Regex.Split(File.ReadAllText(f), TypeStart).Select(t => new SourceUnit(f, DeclaredName(t), t)))
            .ToArray();

    private const string TypeStart =
        @"(?m)^(?<!\][ \t]*\r?\n)(?=(?:[ \t]*\[[^\n]*\][ \t]*\r?\n)*[ \t]*(?:\[[^\n]*\][ \t]*)*(?:(?:public|internal|private|protected|sealed|abstract|static|partial|file|readonly)[ \t]+)*(?:class|record|struct|interface)[ \t]+[A-Za-z_])";

    private static string? DeclaredName(string text) =>
        Regex.Match(text, @"\b(class|record|struct|interface)\s+(?<name>[A-Za-z_]\w*)") is { Success: true } m ? m.Groups["name"].Value : null;

    // The type declarations that set up the store; a database store's setup is not the in-memory one.
    private static SourceUnit[] StoreUnits(string store)
    {
        string[] databaseStores = Stores.Where(static s => s.Key != "in-memory").Select(static s => s.Value).ToArray();
        return Units()
            .Where(u => Regex.IsMatch(u.Text, Stores[store]))
            .Where(u => store != "in-memory" || !databaseStores.Any(d => Regex.IsMatch(u.Text, d)))
            .ToArray();
    }

    // The text of the store's declarations, with the declarations of the types they use, two levels deep.
    private static string StoreText(string store)
    {
        SourceUnit[] units = Units();
        ILookup<string, SourceUnit> declarations = units.Where(static u => u.Name is not null).ToLookup(static u => u.Name!, StringComparer.Ordinal);
        var included = new HashSet<SourceUnit>(StoreUnits(store));
        Assert.True(included.Count > 0, "No consumer test sets up the " + store + " job store.");
        for (int depth = 0; depth < 2; depth++)
        {
            string[] used = included.SelectMany(static u => Regex.Matches(u.Text, @"\b[A-Za-z_]\w*\b").Select(static m => m.Value)).Distinct(StringComparer.Ordinal).ToArray();
            foreach (SourceUnit declared in used.SelectMany(n => declarations[n].Concat(declarations[n + "Attribute"])).ToArray())
            {
                included.Add(declared);
            }
        }
        return string.Join("\n", included.Select(static u => u.Text));
    }

    private sealed record SourceUnit(string File, string? Name, string Text);
}
