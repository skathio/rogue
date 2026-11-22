using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The Postgres job store ships as its own package: packable, on net8.0 and net10.0 only, with Npgsql
/// pinned centrally, every package in its restore graph allow-listed with its license, and listed by
/// the license check; it uses no reflection and keeps its public API tracked. Its integration tests run
/// the shared store contract against a Postgres container from Testcontainers, which never reaches a
/// shipped package, one container per xunit collection, in a project that CI runs.
/// </summary>
public sealed class JobsPostgresRulesTests
{
    private const string PackageDir = "src/SkathIO.Rogue.Jobs.Postgres";
    private const string PackageProject = PackageDir + "/SkathIO.Rogue.Jobs.Postgres.csproj";
    private const string AllowListPath = ".github/allowed-packages.txt";

    [Fact]
    public void PostgresPackage_IsAPackableProject()
    {
        Assert.True(File.Exists(Path.Combine(RepoFiles.Root, PackageProject)), PackageProject + " does not exist.");

        Assert.Contains(PackageProject, RepoFiles.PackableProjects());
    }

    [Fact]
    public void PostgresPackage_TargetsNet8AndNet10Only()
    {
        Match frameworks = Regex.Match(ReadPackage(), @"<TargetFrameworks?>\s*(?<list>[^<]*)</TargetFrameworks?>");

        Assert.True(frameworks.Success, "The project names no target framework.");
        string[] targets = frameworks.Groups["list"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(new[] { "net10.0", "net8.0" }, targets.OrderBy(static t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void PostgresPackage_ReferencesNpgsql_WithItsVersionPinnedCentrally()
    {
        string project = ReadPackage();

        Assert.Matches(@"<PackageReference\s+Include=""Npgsql""", project);
        Assert.DoesNotMatch(@"<PackageReference\s+Include=""Npgsql""[^>]*\bVersion\s*=", project);
        Assert.Matches(@"<PackageVersion\s+Include=""Npgsql""\s+Version=""[^""]+""", RepoFiles.Read("Directory.Packages.props"));
    }

    [Fact]
    public void PostgresPackage_IsCheckedForTrimAndAotSafety()
    {
        Assert.Matches(@"<(IsAotCompatible|IsTrimmable)>\s*true\s*</\1>", ReadPackage());
    }

    [Fact]
    public void EveryPackageInThePostgresRestoreGraph_IsAllowListed_WithItsLicenseAnnotation()
    {
        Dictionary<string, string> allowed = AllowList();
        string[] graph = RestoreGraph(PackageProject);

        string[] unannotated = graph.Where(id => !allowed.TryGetValue(id, out string? note) || !IsLicenseNote(id, note)).ToArray();

        Assert.Contains("Npgsql", graph, StringComparer.OrdinalIgnoreCase);
        Assert.True(unannotated.Length == 0, "Not allow-listed with a '# <id> — <license> — verified <date>' note: " + string.Join(", ", unannotated));
    }

    [Fact]
    public void LicenseCheck_AndTheAllowListHeader_NameThePostgresPackage()
    {
        Match list = Regex.Match(RepoFiles.Read(".github/workflows/ci.yml"), @"packable_projects=\((?<list>.*?)\)", RegexOptions.Singleline);
        string header = string.Join("\n", File.ReadLines(Path.Combine(RepoFiles.Root, AllowListPath)).TakeWhile(static l => l.StartsWith('#')));

        Assert.True(list.Success, "ci.yml has no packable_projects list.");
        Assert.Contains("\"" + PackageProject + "\"", list.Groups["list"].Value, StringComparison.Ordinal);
        Assert.Contains(PackageProject, header, StringComparison.Ordinal);
    }

    [Fact]
    public void Testcontainers_IsInNoPackableProjectsRestoreGraph()
    {
        string[] leaks = RepoFiles.PackableProjects()
            .SelectMany(p => RestoreGraph(p).Where(IsTestcontainers).Select(id => p + ": " + id))
            .ToArray();

        Assert.True(leaks.Length == 0, "Testcontainers is for tests only, yet ships with:\n" + string.Join("\n", leaks));
    }

    [Fact]
    public void PostgresPackageSource_UsesNoReflectionAndNoBannedSerializer()
    {
        string[] banned = { "System.Reflection", "BinaryFormatter", "NetDataContractSerializer", "Activator.CreateInstance", "Type.GetType(", "Assembly.Load" };

        string[] hits = PackageSources()
            .SelectMany(f => banned.Where(b => File.ReadAllText(f).Contains(b, StringComparison.Ordinal)).Select(b => Relative(f) + ": " + b))
            .ToArray();

        Assert.NotEmpty(PackageSources());
        Assert.True(hits.Length == 0, "Banned API in the Postgres package:\n" + string.Join("\n", hits));
        Assert.DoesNotMatch(@"<PackageReference\s+Include=""(MediatR|Mediator)", ReadPackage());
    }

    [Fact]
    public void PostgresPackage_TracksItsPublicApi_IncludingTheTransactionalEnqueue()
    {
        ReadPackage();
        string[] unshipped = Directory.GetFiles(Path.Combine(RepoFiles.Root, PackageDir), "PublicAPI.Unshipped.txt", SearchOption.AllDirectories)
            .Where(static f => !IsBuildOutput(f))
            .ToArray();

        Assert.NotEmpty(unshipped);
        Assert.All(unshipped, f => Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(f)!, "PublicAPI.Shipped.txt")), Relative(f) + " has no PublicAPI.Shipped.txt beside it."));
        // Released entries move to PublicAPI.Shipped.txt, so the member counts as tracked in either file.
        Assert.Contains(unshipped.SelectMany(f => File.ReadLines(f).Concat(File.ReadLines(Path.Combine(Path.GetDirectoryName(f)!, "PublicAPI.Shipped.txt")))), static l => l.Contains("Npgsql.NpgsqlTransaction", StringComparison.Ordinal) || l.Contains("Npgsql.NpgsqlConnection", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"\bFOR\s+UPDATE\s+SKIP\s+LOCKED\b", "the batched claim")]
    [InlineData(@"\bBeginBinaryImport(Async)?\b", "the binary COPY of EnqueueMany")]
    [InlineData(@"\bLISTEN\s+[""\w]", "the wake-up on LISTEN")]
    [InlineData(@"\b(NOTIFY\s+[""\w]|pg_notify\s*\()", "the wake-up on NOTIFY")]
    public void PostgresStore_UsesTheMechanismTheRequestNames(string pattern, string purpose)
    {
        bool used = PackageSources().Any(f => Regex.IsMatch(File.ReadAllText(f), pattern, RegexOptions.IgnoreCase));

        Assert.True(used, "No source in the Postgres package matches '" + pattern + "' for " + purpose + ".");
    }

    [Fact]
    public void PostgresIntegrationTests_RunTheSharedStoreContract()
    {
        string[] sources = IntegrationProjects().SelectMany(TestSources).ToArray();

        Assert.Contains(sources, static f => Regex.IsMatch(File.ReadAllText(f), @"class\s+\w+\s*(\([^)]*\))?\s*:\s*(SkathIO\.Rogue\.Jobs\.StoreContract\.)?JobStoreContract\b"));
    }

    [Fact]
    public void PostgresIntegrationTests_AreTestsOnly_AndUseTestcontainers()
    {
        string[] projects = IntegrationProjects();

        Assert.All(projects, p => Assert.DoesNotMatch(@"<IsPackable>\s*true\s*</IsPackable>", RepoFiles.Read(p)));
        Assert.Contains(projects, p => Regex.IsMatch(RepoFiles.Read(p), @"<PackageReference\s+Include=""Testcontainers[^""]*""\s*/?>"));
        Assert.Contains(projects.SelectMany(RestoreGraph), IsTestcontainers);
    }

    [Fact]
    public void PostgresIntegrationTests_ShareOneContainerPerCollection()
    {
        string text = string.Join("\n", IntegrationProjects().SelectMany(TestSources).Select(File.ReadAllText));
        int collections = Regex.Matches(text, @"\bICollectionFixture<").Count;
        int containers = Regex.Matches(text, @"\bnew\s+(PostgreSqlBuilder|ContainerBuilder)\b").Count;

        Assert.True(collections > 0, "No xunit collection fixture holds the Postgres container.");
        Assert.InRange(containers, 1, collections);
    }

    [Fact]
    public void PostgresIntegrationTests_RunInCi()
    {
        string solution = RepoFiles.Read("SkathIO.Rogue.sln").Replace('\\', '/');
        string ci = RepoFiles.Read(".github/workflows/ci.yml");

        Assert.All(IntegrationProjects(), p => Assert.True(
            solution.Contains(p, StringComparison.Ordinal) || ci.Contains(Path.GetDirectoryName(p)!.Replace('\\', '/'), StringComparison.Ordinal),
            p + " is neither in the solution the ci job tests nor run by a job of its own."));
    }

    private static string ReadPackage()
    {
        Assert.True(File.Exists(Path.Combine(RepoFiles.Root, PackageProject)), PackageProject + " does not exist.");
        return RepoFiles.Read(PackageProject);
    }

    private static bool IsTestcontainers(string id) => id.StartsWith("Testcontainers", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildOutput(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal) || normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string Relative(string path) => Path.GetRelativePath(RepoFiles.Root, path).Replace('\\', '/');

    private static string[] PackageSources()
    {
        string dir = Path.Combine(RepoFiles.Root, PackageDir);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).Where(static f => !IsBuildOutput(f)).ToArray()
            : Array.Empty<string>();
    }

    private static IEnumerable<string> TestSources(string project) =>
        Directory.GetFiles(Path.Combine(RepoFiles.Root, Path.GetDirectoryName(project)!), "*.cs", SearchOption.AllDirectories)
            .Where(static f => !IsBuildOutput(f));

    // Test projects that reference the Postgres package, as "tests/X/X.csproj".
    private static string[] IntegrationProjects()
    {
        string[] projects = Directory.GetFiles(Path.Combine(RepoFiles.Root, "tests"), "*.csproj", SearchOption.AllDirectories)
            .Where(static p => !IsBuildOutput(p))
            .Where(static p => Regex.IsMatch(File.ReadAllText(p), @"<ProjectReference\s+Include=""[^""]*SkathIO\.Rogue\.Jobs\.Postgres\.csproj"""))
            .Select(Relative)
            .ToArray();
        Assert.True(projects.Length > 0, "No test project references the Postgres package.");
        return projects;
    }

    private static bool IsLicenseNote(string id, string note) =>
        note.StartsWith("# " + id + " — ", StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(note, @"verified \d{4}-\d{2}-\d{2}");

    // Each allow-listed id, with the comment lines directly above it.
    private static Dictionary<string, string> AllowList()
    {
        var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var comment = new List<string>();
        foreach (string line in File.ReadLines(Path.Combine(RepoFiles.Root, AllowListPath)).Select(static l => l.Trim()))
        {
            if (line.StartsWith('#'))
            {
                comment.Add(line);
                continue;
            }
            if (line.Length > 0)
            {
                notes[line] = string.Join("\n", comment);
            }
            comment.Clear();
        }
        return notes;
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
