using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The dependency and contract rules the SQL Server job store must keep beyond its packaging. Over the
/// jobs core it adds only the Microsoft.Data.SqlClient tree, and its tests add only Testcontainers and
/// that tree over the Postgres tests. Every package of the tree is allow-listed under a permissive
/// license, except Microsoft.Data.SqlClient.SNI.runtime, which the maintainer accepted as a documented
/// exception in the allow-list and in the governance dependency policy. The shared store contract runs
/// against the SQL Server store itself, the store exposes the public requeue of dead-lettered jobs, and
/// no pattern it validates input with ends in a bare <c>$</c>, which would let a trailing newline through.
/// </summary>
public sealed class JobsSqlServerPolicyTests
{
    private const string PackageDir = "src/SkathIO.Rogue.Jobs.SqlServer";
    private const string PackageProject = PackageDir + "/SkathIO.Rogue.Jobs.SqlServer.csproj";
    private const string JobsCoreProject = "src/SkathIO.Rogue.Jobs/SkathIO.Rogue.Jobs.csproj";
    private const string PostgresTestsProject = "tests/SkathIO.Rogue.Jobs.Postgres.Tests/SkathIO.Rogue.Jobs.Postgres.Tests.csproj";
    private const string SqlClient = "Microsoft.Data.SqlClient";
    private const string SniRuntime = "Microsoft.Data.SqlClient.SNI.runtime";

    // The permissive licenses docs/governance.md "Dependency policy" names.
    private static readonly string[] _permissive = { "MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "MS-PL", "0BSD", "Unlicense" };

    [Fact]
    public void SqlServerPackage_AddsOnlyTheSqlClientTree_OverTheJobsCore()
    {
        PackageGraph package = PackageGraph.Of(PackageProject);
        HashSet<string> tree = package.ClosureOf(static id => id.Equals(SqlClient, StringComparison.OrdinalIgnoreCase));

        string[] unapproved = package.Ids.Except(PackageGraph.Of(JobsCoreProject).Ids, StringComparer.OrdinalIgnoreCase)
            .Where(id => !tree.Contains(id))
            .ToArray();

        Assert.Contains(SqlClient, package.Ids, StringComparer.OrdinalIgnoreCase);
        Assert.True(unapproved.Length == 0, "Packages the maintainer has not approved for the SQL Server store: " + string.Join(", ", unapproved));
    }

    [Fact]
    public void SqlServerTests_AddOnlyTestcontainersAndTheSqlClientTree_OverThePostgresTests()
    {
        HashSet<string> baseline = PackageGraph.Of(PostgresTestsProject).Ids;

        Assert.All(IntegrationProjects(), project =>
        {
            PackageGraph graph = PackageGraph.Of(project);
            HashSet<string> approved = graph.ClosureOf(static id =>
                id.Equals(SqlClient, StringComparison.OrdinalIgnoreCase) || id.StartsWith("Testcontainers", StringComparison.OrdinalIgnoreCase));
            string[] unapproved = graph.Ids.Except(baseline, StringComparer.OrdinalIgnoreCase).Where(id => !approved.Contains(id)).ToArray();

            Assert.True(unapproved.Length == 0, project + " pulls in packages the maintainer has not approved: " + string.Join(", ", unapproved));
        });
    }

    [Fact]
    public void SqlClientTree_IsAllowListed_UnderAPermissiveLicense_ExceptTheSniRuntime()
    {
        PackageGraph package = PackageGraph.Of(PackageProject);
        Dictionary<string, string> notes = AllowListNotes();
        string[] added = package.Ids.Except(PackageGraph.Of(JobsCoreProject).Ids, StringComparer.OrdinalIgnoreCase)
            .Where(static id => !id.Equals(SniRuntime, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        string[] notPermissive = added.Where(id => !notes.TryGetValue(id, out string? note) || !HasPermissiveLicense(id, note)).ToArray();

        Assert.NotEmpty(added);
        Assert.True(notPermissive.Length == 0, "Not allow-listed under a permissive license: " + string.Join(", ", notPermissive));
    }

    [Fact]
    public void SniRuntime_IsAllowListed_AsADocumentedException()
    {
        Dictionary<string, string> notes = AllowListNotes();

        Assert.True(notes.TryGetValue(SniRuntime, out string? note), SniRuntime + " is not on the allow-list.");
        Assert.Matches(@"(?m)^# " + Regex.Escape(SniRuntime) + @" — .+ — verified \d{4}-\d{2}-\d{2}", note);
        Assert.Contains("exception", note, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SniRuntime)]
    [InlineData("SkathIO.Rogue.Jobs.SqlServer")]
    public void GovernanceDependencyPolicy_NamesTheSqlServerPackageAndItsLicenseException(string name)
    {
        Match policy = Regex.Match(RepoFiles.Read("docs/governance.md"), @"^## Dependency policy\s*$(?<body>.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline);

        Assert.True(policy.Success, "docs/governance.md has no \"Dependency policy\" section.");
        Assert.Contains(name, policy.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreContract_RunsAgainstTheSqlServerStore()
    {
        string[] contracts = IntegrationProjects()
            .SelectMany(TestSources)
            .Where(static f => Regex.IsMatch(File.ReadAllText(f), @"class\s+\w+\s*(\([^)]*\))?\s*:\s*(SkathIO\.Rogue\.Jobs\.StoreContract\.)?JobStoreContract\b"))
            .ToArray();

        Assert.NotEmpty(contracts);
        Assert.All(contracts, f =>
        {
            string text = File.ReadAllText(f);
            Assert.Matches(@"\bSqlServer\w*Store\b", text);
            Assert.DoesNotContain("InMemoryJobStore", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SqlServerStore_ExposesThePublicRequeueOfDeadLetteredJobs()
    {
        string unshipped = Path.Combine(RepoFiles.Root, PackageDir, "PublicAPI.Unshipped.txt");
        string shipped = Path.Combine(RepoFiles.Root, PackageDir, "PublicAPI.Shipped.txt");

        Assert.True(File.Exists(unshipped), PackageDir + "/PublicAPI.Unshipped.txt does not exist.");
        Assert.True(File.Exists(shipped), PackageDir + "/PublicAPI.Shipped.txt does not exist.");
        // Released entries move to PublicAPI.Shipped.txt, so the member counts as tracked in either file.
        Assert.Contains(File.ReadLines(unshipped).Concat(File.ReadLines(shipped)), static l => Regex.IsMatch(l, @"^SkathIO\.Rogue\.Jobs\.SqlServer\.\w+\.RequeueAsync\("));
    }

    [Fact]
    public void SqlServerPackage_EndsNoValidationPatternWithABareDollar()
    {
        string dir = Path.Combine(RepoFiles.Root, PackageDir);
        Assert.True(Directory.Exists(dir), PackageDir + " does not exist.");

        string[] hits = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(static f => !IsBuildOutput(f))
            .SelectMany(static f => File.ReadLines(f).Select(l => (File: f, Line: l)))
            .Where(static x => x.Line.Contains("Regex", StringComparison.Ordinal) && Regex.IsMatch(x.Line, @"(?<!\\)\$""\s*[,)]"))
            .Select(static x => Path.GetRelativePath(RepoFiles.Root, x.File) + ": " + x.Line.Trim())
            .ToArray();

        Assert.True(hits.Length == 0, @"Anchor with \z, not $, which accepts a trailing newline:" + "\n" + string.Join("\n", hits));
    }

    private static bool HasPermissiveLicense(string id, string note)
    {
        Match line = Regex.Match(note, @"(?m)^# " + Regex.Escape(id) + @" — (?<license>.+?) — verified \d{4}-\d{2}-\d{2}");
        return line.Success && _permissive.Any(p => Regex.IsMatch(line.Groups["license"].Value, @"(?<![\w.-])" + Regex.Escape(p) + @"(?![\w.-])"));
    }

    private static bool IsBuildOutput(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal) || normalized.Contains("/obj/", StringComparison.Ordinal);
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
            .Select(static p => Path.GetRelativePath(RepoFiles.Root, p).Replace('\\', '/'))
            .ToArray();
        Assert.True(projects.Length > 0, "No test project references the SQL Server package.");
        return projects;
    }

    // Each allow-listed id, with the comment lines directly above it.
    private static Dictionary<string, string> AllowListNotes()
    {
        var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var comment = new List<string>();
        foreach (string line in File.ReadLines(Path.Combine(RepoFiles.Root, ".github/allowed-packages.txt")).Select(static l => l.Trim()))
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

    /// <summary>The packages in a project's restore graph, every target, with what each depends on.</summary>
    private sealed class PackageGraph
    {
        private readonly Dictionary<string, HashSet<string>> _dependencies;

        private PackageGraph(Dictionary<string, HashSet<string>> dependencies) => _dependencies = dependencies;

        public HashSet<string> Ids => new HashSet<string>(_dependencies.Keys, StringComparer.OrdinalIgnoreCase);

        public static PackageGraph Of(string project)
        {
            string assets = Path.Combine(RepoFiles.Root, Path.GetDirectoryName(project)!, "obj", "project.assets.json");
            Assert.True(File.Exists(assets), assets + " is missing; restore the solution first.");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));
            var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<JsonProperty> packages = document.RootElement.GetProperty("targets").EnumerateObject()
                .SelectMany(static t => t.Value.EnumerateObject())
                .Where(static l => l.Value.GetProperty("type").GetString() == "package");
            foreach (JsonProperty library in packages)
            {
                Add(dependencies, library);
            }
            return new PackageGraph(dependencies);
        }

        private static void Add(Dictionary<string, HashSet<string>> dependencies, JsonProperty library)
        {
            string id = library.Name.Split('/')[0];
            if (!dependencies.TryGetValue(id, out HashSet<string>? uses))
            {
                uses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                dependencies[id] = uses;
            }
            if (library.Value.TryGetProperty("dependencies", out JsonElement used))
            {
                uses.UnionWith(used.EnumerateObject().Select(static d => d.Name));
            }
        }

        /// <summary>The packages matching <paramref name="isRoot"/> and everything they pull in.</summary>
        public HashSet<string> ClosureOf(Func<string, bool> isRoot)
        {
            var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>(_dependencies.Keys.Where(isRoot));
            while (pending.Count > 0)
            {
                string id = pending.Dequeue();
                if (closure.Add(id) && _dependencies.TryGetValue(id, out HashSet<string>? uses))
                {
                    foreach (string next in uses)
                    {
                        pending.Enqueue(next);
                    }
                }
            }
            return closure;
        }
    }
}
