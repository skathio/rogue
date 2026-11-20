using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The SQL Server job store ships as its own package: packable, on net8.0 and net10.0 only, AOT-checked,
/// with Microsoft.Data.SqlClient pinned centrally, every package of the SqlClient tree allow-listed with
/// its license, and listed by the license check. It claims batches under READPAST and UPDLOCK, bulk
/// enqueues through SqlBulkCopy or a table-valued parameter, keeps a filtered unique index and takes
/// sp_getapplock, uses no reflection and tracks its public API. Its integration tests run the shared
/// store contract against a SQL Server container from Testcontainers, one container per xunit
/// collection, skipped without Docker except in CI, in a project that CI runs.
/// </summary>
public sealed class JobsSqlServerRulesTests
{
    private const string PackageDir = "src/SkathIO.Rogue.Jobs.SqlServer";
    private const string PackageProject = PackageDir + "/SkathIO.Rogue.Jobs.SqlServer.csproj";
    private const string AllowListPath = ".github/allowed-packages.txt";
    private const string SqlClient = "Microsoft.Data.SqlClient";

    [Fact]
    public void SqlServerPackage_IsAPackableProject()
    {
        Assert.True(File.Exists(Path.Combine(RepoFiles.Root, PackageProject)), PackageProject + " does not exist.");

        Assert.Contains(PackageProject, RepoFiles.PackableProjects());
    }

    [Fact]
    public void SqlServerPackage_TargetsNet8AndNet10Only()
    {
        Match frameworks = Regex.Match(ReadPackage(), @"<TargetFrameworks?>\s*(?<list>[^<]*)</TargetFrameworks?>");

        Assert.True(frameworks.Success, "The project names no target framework.");
        string[] targets = frameworks.Groups["list"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(new[] { "net10.0", "net8.0" }, targets.OrderBy(static t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void SqlServerPackage_ReferencesSqlClient_WithItsVersionPinnedCentrally()
    {
        string project = ReadPackage();

        Assert.Matches(@"<PackageReference\s+Include=""Microsoft\.Data\.SqlClient""", project);
        Assert.DoesNotMatch(@"<PackageReference\s+Include=""[^""]+""[^>]*\bVersion\s*=", project);
        Assert.Matches(@"<PackageVersion\s+Include=""Microsoft\.Data\.SqlClient""\s+Version=""[^""]+""", RepoFiles.Read("Directory.Packages.props"));
    }

    [Fact]
    public void SqlServerPackage_IsCheckedForTrimAndAotSafety()
    {
        Assert.Matches(@"<(IsAotCompatible|IsTrimmable)>\s*true\s*</\1>", ReadPackage());
    }

    [Fact]
    public void EveryPackageInTheSqlServerRestoreGraph_IsAllowListed_WithItsLicenseAnnotation()
    {
        Dictionary<string, string> allowed = AllowList();
        string[] graph = RestoreGraph(PackageProject);

        string[] unannotated = graph.Where(id => !allowed.TryGetValue(id, out string? note) || !IsLicenseNote(id, note)).ToArray();

        Assert.Contains(SqlClient, graph, StringComparer.OrdinalIgnoreCase);
        Assert.True(unannotated.Length == 0, "Not allow-listed with a '# <id> — <license> — verified <date>' note: " + string.Join(", ", unannotated));
    }

    [Fact]
    public void LicenseCheck_AndTheAllowListHeader_NameTheSqlServerPackage()
    {
        Match list = Regex.Match(RepoFiles.Read(".github/workflows/ci.yml"), @"packable_projects=\((?<list>.*?)\)", RegexOptions.Singleline);
        string header = string.Join("\n", File.ReadLines(Path.Combine(RepoFiles.Root, AllowListPath)).TakeWhile(static l => l.StartsWith('#')));

        Assert.True(list.Success, "ci.yml has no packable_projects list.");
        Assert.Contains("\"" + PackageProject + "\"", list.Groups["list"].Value, StringComparison.Ordinal);
        Assert.Contains(PackageProject, header, StringComparison.Ordinal);
    }

    [Fact]
    public void Testcontainers_IsNotInTheSqlServerPackagesRestoreGraph()
    {
        string[] leaks = RestoreGraph(PackageProject).Where(IsTestcontainers).ToArray();

        Assert.True(leaks.Length == 0, "Testcontainers is for tests only, yet ships with the SQL Server package: " + string.Join(", ", leaks));
    }

    [Fact]
    public void SqlServerPackageSource_UsesNoReflectionAndNoBannedSerializer()
    {
        string[] banned = { "System.Reflection", "BinaryFormatter", "NetDataContractSerializer", "Activator.CreateInstance", "Type.GetType(", "Assembly.Load" };
        string[] sources = PackageSources("*.cs");

        string[] hits = sources
            .SelectMany(f => banned.Where(b => File.ReadAllText(f).Contains(b, StringComparison.Ordinal)).Select(b => Relative(f) + ": " + b))
            .ToArray();

        Assert.NotEmpty(sources);
        Assert.True(hits.Length == 0, "Banned API in the SQL Server package:\n" + string.Join("\n", hits));
        Assert.DoesNotMatch(@"<PackageReference\s+Include=""(MediatR|Mediator)", ReadPackage());
    }

    [Fact]
    public void SqlServerPackage_TracksItsPublicApi()
    {
        ReadPackage();
        string[] unshipped = Directory.GetFiles(Path.Combine(RepoFiles.Root, PackageDir), "PublicAPI.Unshipped.txt", SearchOption.AllDirectories)
            .Where(static f => !IsBuildOutput(f))
            .ToArray();

        Assert.NotEmpty(unshipped);
        Assert.All(unshipped, f => Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(f)!, "PublicAPI.Shipped.txt")), Relative(f) + " has no PublicAPI.Shipped.txt beside it."));
        Assert.Contains(unshipped.SelectMany(File.ReadLines), static l => l.Contains("SkathIO.Rogue.Jobs.SqlServer.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"\bREADPAST\b", "the batched claim's READPAST hint")]
    [InlineData(@"\bUPDLOCK\b", "the batched claim's UPDLOCK hint")]
    [InlineData(@"\bTOP\s*\(", "the batched claim taking up to a batch of jobs")]
    [InlineData(@"\bSqlBulkCopy\b|\bSqlDbType\.Structured\b|\bAS\s+TABLE\b", "the bulk enqueue through SqlBulkCopy or a table-valued parameter")]
    [InlineData(@"\bCREATE\s+UNIQUE\s+(NONCLUSTERED\s+|CLUSTERED\s+)?INDEX\b[^;]*?\bWHERE\b", "the filtered unique index")]
    [InlineData(@"\bsp_getapplock\b", "the application lock")]
    public void SqlServerStore_UsesTheMechanismTheRequestNames(string pattern, string purpose)
    {
        string[] sources = PackageSources("*.cs").Concat(PackageSources("*.sql")).ToArray();

        bool used = sources.Any(f => Regex.IsMatch(File.ReadAllText(f), pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline));

        Assert.True(used, "No source in the SQL Server package matches '" + pattern + "' for " + purpose + ".");
    }

    [Fact]
    public void SqlServerIntegrationTests_RunTheSharedStoreContract_SkippedOnlyWithoutDockerOutsideCi()
    {
        string[] sources = IntegrationProjects().SelectMany(TestSources).ToArray();

        string[] contracts = sources
            .Where(static f => Regex.IsMatch(File.ReadAllText(f), @"class\s+\w+\s*(\([^)]*\))?\s*:\s*(SkathIO\.Rogue\.Jobs\.StoreContract\.)?JobStoreContract\b"))
            .ToArray();

        Assert.NotEmpty(contracts);
        Assert.All(contracts, f => Assert.Matches(@"\[\s*(SkathIO\.Rogue\.Jobs\.StoreContract\.)?RequiresDocker(Attribute)?\s*\]", File.ReadAllText(f)));
    }

    [Fact]
    public void SqlServerIntegrationTests_AreTestsOnly_AndUseTestcontainers()
    {
        string[] projects = IntegrationProjects();

        Assert.All(projects, p => Assert.DoesNotMatch(@"<IsPackable>\s*true\s*</IsPackable>", RepoFiles.Read(p)));
        Assert.Contains(projects, p => Regex.IsMatch(RepoFiles.Read(p), @"<PackageReference\s+Include=""Testcontainers[^""]*""\s*/?>"));
        Assert.Contains(projects.SelectMany(RestoreGraph), IsTestcontainers);
    }

    [Fact]
    public void SqlServerIntegrationTests_ShareOneContainerPerCollection()
    {
        string text = string.Join("\n", IntegrationProjects().SelectMany(TestSources).Select(File.ReadAllText));
        int collections = Regex.Matches(text, @"\bICollectionFixture<").Count;
        int containers = Regex.Matches(text, @"\bnew\s+(MsSqlBuilder|ContainerBuilder)\b").Count;

        Assert.True(collections > 0, "No xunit collection fixture holds the SQL Server container.");
        Assert.InRange(containers, 1, collections);
    }

    [Fact]
    public void SqlServerIntegrationTests_RunInCi()
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

    private static string[] PackageSources(string pattern)
    {
        string dir = Path.Combine(RepoFiles.Root, PackageDir);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, pattern, SearchOption.AllDirectories).Where(static f => !IsBuildOutput(f)).ToArray()
            : Array.Empty<string>();
    }

    private static IEnumerable<string> TestSources(string project) =>
        Directory.GetFiles(Path.Combine(RepoFiles.Root, Path.GetDirectoryName(project)!), "*.cs", SearchOption.AllDirectories)
            .Where(static f => !IsBuildOutput(f));

    // Test projects that reference the SQL Server package, as "tests/X/X.csproj".
    private static string[] IntegrationProjects()
    {
        string[] projects = Directory.GetFiles(Path.Combine(RepoFiles.Root, "tests"), "*.csproj", SearchOption.AllDirectories)
            .Where(static p => !IsBuildOutput(p))
            .Where(static p => Regex.IsMatch(File.ReadAllText(p), @"<ProjectReference\s+Include=""[^""]*SkathIO\.Rogue\.Jobs\.SqlServer\.csproj"""))
            .Select(Relative)
            .ToArray();
        Assert.True(projects.Length > 0, "No test project references the SQL Server package.");
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
