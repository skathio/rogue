using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SkathIO.Rogue.Jobs.Stores;
using Xunit;

namespace SkathIO.Rogue.Jobs.SqlServer.Tests;

/// <summary>
/// Hangfire.Core 1.8.25 and a direct pin of Newtonsoft.Json 13.0.4 (replacing the vulnerable 11.0.1)
/// are approved for the jobs bench only and must never reach a packable project. The worker is opt-in
/// through AddRogueJobWorker, and that holds for a host on the SQL Server store too.
/// </summary>
public sealed class BenchOnlyDependenciesAndWorkerOptInTests
{
    private const string BenchProject = "bench/SkathIO.Rogue.Benchmarks.Jobs/SkathIO.Rogue.Benchmarks.Jobs.csproj";
    private const string NewtonsoftJson = "Newtonsoft.Json";
    private const string ConnectionString = "Server=localhost;Database=jobs;TrustServerCertificate=True";
    private static readonly Lazy<string> _root = new Lazy<string>(FindRoot);

    [Theory]
    [InlineData("Hangfire.Core")]
    [InlineData(NewtonsoftJson)]
    public void BenchOnlyPackage_IsInNoPackableProjectsRestoreGraph(string package)
    {
        string[] packable = PackableProjects();

        string[] leaks = packable
            .Where(p => RestoreGraph(p).Any(l => l.Id.Equals(package, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Contains("src/SkathIO.Rogue.Jobs.SqlServer/SkathIO.Rogue.Jobs.SqlServer.csproj", packable);
        Assert.True(leaks.Length == 0, package + " is bench only, yet ships with: " + string.Join(", ", leaks));
    }

    [Theory]
    [InlineData("Hangfire.Core", "1.8.25")]
    [InlineData(NewtonsoftJson, "13.0.4")]
    public void JobsBench_ResolvesTheApprovedVersion_AndNoOther(string package, string version)
    {
        string[] resolved = RestoreGraph(BenchProject)
            .Where(l => l.Id.Equals(package, StringComparison.OrdinalIgnoreCase))
            .Select(static l => l.Version)
            .ToArray();

        Assert.Equal(new[] { version }, resolved);
    }

    [Fact]
    public void NewtonsoftJson_IsPinnedCentrally_AndReferencedDirectlyByTheJobsBench()
    {
        string bench = Read(BenchProject);

        Assert.Matches(@"<PackageVersion\s+Include=""Newtonsoft\.Json""\s+Version=""13\.0\.4""", Read("Directory.Packages.props"));
        Assert.Matches(@"<PackageReference\s+Include=""Newtonsoft\.Json""\s*/>", bench);
    }

    [Theory]
    [InlineData(@"Hangfire[^""]*")]
    [InlineData(@"Newtonsoft\.Json")]
    public void BenchOnlyPackage_IsReferencedFromNoProjectOutsideBench(string package)
    {
        string[] outside = new[] { "src", "tests", "samples" }
            .Select(static d => Path.Combine(_root.Value, d))
            .Where(Directory.Exists)
            .SelectMany(static d => Directory.GetFiles(d, "*.csproj", SearchOption.AllDirectories))
            .Where(p => Regex.IsMatch(File.ReadAllText(p), @"<PackageReference\s+Include=""" + package + @""""))
            .Select(Relative)
            .ToArray();

        Assert.True(outside.Length == 0, "Bench-only package referenced by: " + string.Join(", ", outside));
    }

    [Fact]
    public void SqlServerHost_WithoutTheWorkerCall_HostsNoWorker()
    {
        var services = new ServiceCollection();
        services.AddRogueJobs();
        services.AddRogueSqlServerJobs(ConnectionString);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.All(provider.GetServices<IHostedService>(), static s => Assert.True(
            IsStoreService(s),
            s.GetType() + " is hosted by an enqueue-only SQL Server host."));
    }

    [Fact]
    public void SqlServerHost_WithTheWorkerCall_HostsAWorker()
    {
        var services = new ServiceCollection();
        services.AddRogueJobs();
        services.AddRogueSqlServerJobs(ConnectionString);
        services.AddRogueJobWorker();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Contains(provider.GetServices<IHostedService>(), static s => !IsStoreService(s));
    }

    // The store's own background services: the poller that wakes the worker and the pruner.
    private static bool IsStoreService(IHostedService service) => service is SqlServerJobPoller || service is JobPruner;

    private static string Read(string relative) => File.ReadAllText(Path.Combine(_root.Value, relative));

    private static string Relative(string path) => Path.GetRelativePath(_root.Value, path).Replace('\\', '/');

    private static string[] PackableProjects() =>
        Directory.GetFiles(Path.Combine(_root.Value, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(static p => Regex.IsMatch(File.ReadAllText(p), @"<IsPackable>\s*true\s*</IsPackable>", RegexOptions.IgnoreCase))
            .Select(Relative)
            .ToArray();

    // The packages in a project's restore graph, every target framework, as id and version.
    private static (string Id, string Version)[] RestoreGraph(string project)
    {
        string assets = Path.Combine(_root.Value, Path.GetDirectoryName(project)!, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), assets + " is missing; restore the solution first.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));
        return document.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(static l => l.Value.GetProperty("type").GetString() == "package")
            .Select(static l => l.Name.Split('/'))
            .Select(static parts => (parts[0], parts[1]))
            .ToArray();
    }

    private static string FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SkathIO.Rogue.sln")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd('/', '\\'));
        }
        Assert.NotNull(dir);
        return dir;
    }
}
