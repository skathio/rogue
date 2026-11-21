using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The end-to-end consumer suite: a CI job in ci.yml packs all twelve packages into a local feed on
/// every pull request, in parallel to the other jobs, and runs the tests of a separate consumer
/// solution that restores those packages by package reference, as a real app would, never by project
/// reference. Its tests cover the packaging, the generator shipped in the analyzers/ folder, the
/// ROGUE013 version check, Send, Publish and streams with behaviors, and Validation. The consumer
/// keeps the project's conventions and brings in no package the maintainer has not license-checked.
/// </summary>
public sealed class ConsumerSuiteRulesTests
{
    private const string CiPath = ".github/workflows/ci.yml";
    private const string MainSolution = "SkathIO.Rogue.sln";
    private const int ShippedPackageCount = 12;

    // The package ids Directory.Packages.props pinned before the consumer suite. The maintainer checks
    // the license of any other package before it is used, so a new id here follows that check.
    private static readonly string[] _licenseCheckedPackages =
    {
        "MinVer", "Microsoft.SourceLink.GitHub", "Microsoft.CodeAnalysis.Analyzers", "Microsoft.CodeAnalysis.CSharp",
        "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.DependencyInjection", "FluentValidation",
        "Microsoft.Extensions.Logging.Abstractions", "Microsoft.Extensions.Logging", "Microsoft.Extensions.Hosting.Abstractions",
        "Npgsql", "Microsoft.Data.SqlClient", "RabbitMQ.Client", "Microsoft.AspNetCore.Mvc.Testing", "Microsoft.NET.Test.Sdk",
        "xunit", "xunit.runner.visualstudio", "coverlet.collector", "Verify.Xunit", "Testcontainers.PostgreSql",
        "Testcontainers.MsSql", "Testcontainers.RabbitMq", "Microsoft.CodeAnalysis.CSharp.Workspaces", "BenchmarkDotNet",
        "MediatR", "Hangfire.InMemory", "Hangfire.SqlServer", "Newtonsoft.Json", "Microsoft.Extensions.Hosting",
        "Microsoft.CodeAnalysis.PublicApiAnalyzers",
    };

    private static readonly string[] _skippedDirectories = { "bin", "obj", ".git", "node_modules", "BenchmarkDotNet.Artifacts", "TestResults", ".somi", ".claude" };

    public static TheoryData<string> ShippedPackages()
    {
        var data = new TheoryData<string>();
        foreach (string id in RepoFiles.PackableProjects().Select(PackageId))
        {
            data.Add(id);
        }
        return data;
    }

    [Fact]
    public void ThereAreTwelvePackages_ToPack()
    {
        Assert.Equal(ShippedPackageCount, RepoFiles.PackableProjects().Length);
    }

    [Fact]
    public void Ci_HasAJob_ThatPacksThePackages_IntoALocalFeed()
    {
        string job = PackJob();

        Assert.Matches(@"dotnet\s+pack\b[^\n]*\s(-o|--output)\s|PackageOutputPath", job);
    }

    [Fact]
    public void Ci_PacksAllTwelvePackages()
    {
        string job = PackJob();
        string[] projects = RepoFiles.PackableProjects();

        bool packsTheSolution = Regex.IsMatch(job, @"dotnet\s+pack\b[^\n]*" + Regex.Escape(MainSolution))
            || Regex.IsMatch(job, @"dotnet\s+pack(\s*$|\s+-)", RegexOptions.Multiline);
        string[] unnamed = projects.Where(p => !job.Contains(Path.GetFileName(p), StringComparison.Ordinal)).ToArray();

        Assert.Equal(ShippedPackageCount, projects.Length);
        Assert.True(packsTheSolution || unnamed.Length == 0, "The CI pack step packs neither the solution nor these packages: " + string.Join(", ", unnamed));
    }

    [Fact]
    public void Ci_RunsTheConsumerSolutionsTests_InThePackJob()
    {
        string job = PackJob();

        Assert.Matches(@"dotnet\s+test\b", job);
        Assert.False(string.IsNullOrEmpty(ConsumerSolution()));
    }

    [Fact]
    public void Ci_RunsTheConsumerJob_OnEveryPullRequest_InParallelToTheOthers()
    {
        string job = PackJob();
        string trigger = Regex.Match(RepoFiles.Read(CiPath), @"(?ms)^on:\s*$(?<body>.*?)(?=^\S)").Groups["body"].Value;

        Assert.Matches(@"(?m)^\s+pull_request:\s*$", trigger);
        Assert.DoesNotMatch(@"(?m)^\s+(paths|paths-ignore|branches|branches-ignore|types):", PullRequestFilters(trigger));
        Assert.DoesNotMatch(@"(?m)^    (needs|if):", job);
    }

    [Fact]
    public void ConsumerSolution_IsSeparateFromTheMainSolution()
    {
        string main = RepoFiles.Read(MainSolution);
        string[] shared = ConsumerProjects().Where(p => main.Contains(p.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) || main.Contains(p, StringComparison.OrdinalIgnoreCase)).ToArray();

        Assert.NotEqual(MainSolution, ConsumerSolution());
        Assert.NotEmpty(ConsumerProjects());
        Assert.True(shared.Length == 0, MainSolution + " also holds consumer projects: " + string.Join(", ", shared));
    }

    [Fact]
    public void ConsumerProjects_ReferenceNoProjectUnderSrc()
    {
        string src = Path.Combine(RepoFiles.Root, "src") + Path.DirectorySeparatorChar;
        string[] references = ConsumerBuildFiles()
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"<ProjectReference\s+Include=""(?<path>[^""]+)""").Select(m => ResolveFrom(f, m.Groups["path"].Value)))
            .Where(p => p.StartsWith(src, StringComparison.Ordinal))
            .ToArray();

        Assert.True(references.Length == 0, "The consumer restores the packages, yet references these projects: " + string.Join(", ", references));
    }

    [Theory]
    [MemberData(nameof(ShippedPackages))]
    public void ConsumerSolution_RestoresEveryPackage_ByPackageReference(string id)
    {
        Assert.Contains(id, ConsumerPackageReferences(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConsumerProjects_PinNoVersionInTheirCsproj()
    {
        string[] versioned = ConsumerBuildFiles()
            .Where(static f => f.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(static f => Regex.IsMatch(File.ReadAllText(f), @"<PackageReference\s+Include=""[^""]+""[^>]*\bVersion\s*="))
            .ToArray();

        Assert.True(versioned.Length == 0, "Versions belong in a Directory.Packages.props, yet these csproj set one: " + string.Join(", ", versioned));
    }

    [Fact]
    public void ConsumerProjects_KeepNullableOn_AndImplicitUsingsOff()
    {
        string[] files = ConsumerBuildFiles();

        Assert.NotEmpty(files);
        Assert.All(files, static f => Assert.DoesNotMatch(@"<Nullable>\s*disable|<ImplicitUsings>\s*enable", File.ReadAllText(f)));
    }

    [Fact]
    public void ConsumerSolution_UsesOnlyLicenseCheckedPackages()
    {
        string[] notChecked = ConsumerBuildFiles()
            .SelectMany(static f => Regex.Matches(File.ReadAllText(f), @"<(PackageReference|PackageVersion|GlobalPackageReference)\s+(Include|Update)=""(?<id>[^""]+)""").Select(static m => m.Groups["id"].Value))
            .Where(static id => !IsRoguePackage(id) && !_licenseCheckedPackages.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(notChecked.Length == 0, "These packages need the maintainer's license check before use: " + string.Join(", ", notChecked));
    }

    [Fact]
    public void MainBuild_PinsOnlyLicenseCheckedPackages()
    {
        string[] pinned = Regex.Matches(RepoFiles.Read("Directory.Packages.props"), @"<PackageVersion\s+Include=""(?<id>[^""]+)""").Select(static m => m.Groups["id"].Value).ToArray();
        string[] notChecked = pinned.Where(static id => !_licenseCheckedPackages.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray();

        Assert.NotEmpty(pinned);
        Assert.True(notChecked.Length == 0, "These packages need the maintainer's license check before use: " + string.Join(", ", notChecked));
    }

    [Fact]
    public void ConsumerTests_CheckTheGeneratorShipsInTheAnalyzersFolder()
    {
        Assert.Matches(@"\banalyzers\b", ConsumerSources());
    }

    [Fact]
    public void ConsumerTests_CheckTheCoreAndFeatureVersionCheck()
    {
        Assert.Contains("ROGUE013", ConsumerSources() + ConsumerBuildText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\.Send\s*(<[^>]*>)?\s*\(", "Send")]
    [InlineData(@"\.Publish\s*(<[^>]*>)?\s*\(", "Publish")]
    [InlineData(@"\.CreateStream\s*(<[^>]*>)?\s*\(", "streams")]
    [InlineData(@"\bIPipelineBehavior\s*<", "a request behavior")]
    [InlineData(@"\bIStreamPipelineBehavior\s*<", "a stream behavior")]
    public void ConsumerTests_RunTheCoreScenarios(string pattern, string scenario)
    {
        Assert.True(Regex.IsMatch(ConsumerSources(), pattern), "The consumer tests do not cover " + scenario + ".");
    }

    [Fact]
    public void ConsumerTests_RunValidation_ThroughTheValidationPackage()
    {
        Assert.Matches(@"\bSkathIO\.Rogue\.Validation\b", ConsumerSources());
    }

    private static string PackageId(string project) => Path.GetFileNameWithoutExtension(project);

    private static bool IsRoguePackage(string id) =>
        id.Equals("SkathIO.Rogue", StringComparison.OrdinalIgnoreCase) || id.StartsWith("SkathIO.Rogue.", StringComparison.OrdinalIgnoreCase);

    private static string ResolveFrom(string file, string include) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, include.Replace('\\', Path.DirectorySeparatorChar)));

    // The pull_request trigger's own settings, if it has any.
    private static string PullRequestFilters(string trigger) =>
        Regex.Match(trigger, @"(?ms)^(?<indent>[ ]+)pull_request:\s*$(?<body>.*?)(?=^\k<indent>\S|\z)").Groups["body"].Value;

    // The text of each job in ci.yml, split at the two-space-indented job keys.
    private static string[] CiJobs()
    {
        string ci = RepoFiles.Read(CiPath);
        int jobs = ci.IndexOf("\njobs:", StringComparison.Ordinal);
        Assert.True(jobs >= 0, "ci.yml has no jobs.");
        return Regex.Split(ci.Substring(jobs), @"(?m)^(?=  [A-Za-z][\w-]*:\s*$)").Skip(1).ToArray();
    }

    // A job's text with the text of each repository script it runs, two levels deep.
    private static string WithScripts(string text, int depth)
    {
        if (depth == 0)
        {
            return text;
        }
        IEnumerable<string> scripts = Regex.Matches(text, @"(?<![\w.-])(\./)?(?<path>[\w.-]+(/[\w.-]+)*\.(sh|ps1|py|mjs|js|cs))\b")
            .Select(static m => Path.Combine(RepoFiles.Root, m.Groups["path"].Value))
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal);
        return text + "\n" + string.Join("\n", scripts.Select(s => WithScripts(File.ReadAllText(s), depth - 1)));
    }

    // The ci.yml job that packs the packages, with the scripts it runs.
    private static string PackJob()
    {
        string[] packing = CiJobs().Select(static j => WithScripts(j, 2)).Where(static j => Regex.IsMatch(j, @"dotnet\s+pack\b")).ToArray();
        Assert.True(packing.Length > 0, "No job in ci.yml packs the packages.");
        return string.Join("\n", packing);
    }

    private static IEnumerable<string> RepoFilesNamed(string directory, Func<string, bool> match)
    {
        foreach (string file in Directory.EnumerateFiles(directory).Where(match))
        {
            yield return file;
        }
        foreach (string sub in Directory.EnumerateDirectories(directory).Where(static d => !_skippedDirectories.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)))
        {
            foreach (string file in RepoFilesNamed(sub, match))
            {
                yield return file;
            }
        }
    }

    // The solution the pack job runs, as a path relative to the repository root.
    private static string ConsumerSolution()
    {
        string job = PackJob();
        string[] solutions = RepoFilesNamed(RepoFiles.Root, static f => f.EndsWith(".sln", StringComparison.Ordinal) || f.EndsWith(".slnx", StringComparison.Ordinal))
            .Select(static f => Path.GetRelativePath(RepoFiles.Root, f).Replace('\\', '/'))
            .Where(static s => s != MainSolution)
            .Where(s => job.Contains(Path.GetFileName(s), StringComparison.Ordinal) || (Path.GetDirectoryName(s) is { Length: > 0 } dir && job.Contains(dir.Replace('\\', '/'), StringComparison.Ordinal)))
            .ToArray();
        Assert.True(solutions.Length == 1, "Expected one consumer solution run by the CI pack job, found: " + string.Join(", ", solutions));
        return solutions[0];
    }

    // The consumer solution's projects, relative to the repository root.
    private static string[] ConsumerProjects()
    {
        string solution = ConsumerSolution();
        string directory = Path.GetDirectoryName(Path.Combine(RepoFiles.Root, solution))!;
        return Regex.Matches(RepoFiles.Read(solution), @"""(?<path>[^""]+\.csproj)""")
            .Select(m => Path.GetRelativePath(RepoFiles.Root, Path.GetFullPath(Path.Combine(directory, m.Groups["path"].Value.Replace('\\', '/')))).Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    // Every file under each consumer project, and the files beside the consumer solution.
    private static string[] ConsumerFiles(Func<string, bool> match)
    {
        string solutionDirectory = Path.GetDirectoryName(Path.Combine(RepoFiles.Root, ConsumerSolution()))!;
        IEnumerable<string> beside = Directory.EnumerateFiles(solutionDirectory).Where(match);
        IEnumerable<string> inside = ConsumerProjects()
            .Select(static p => Path.GetDirectoryName(Path.Combine(RepoFiles.Root, p))!)
            .Where(Directory.Exists)
            .SelectMany(d => RepoFilesNamed(d, match));
        return beside.Concat(inside).Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] ConsumerBuildFiles() =>
        ConsumerFiles(static f => f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".props", StringComparison.Ordinal) || f.EndsWith(".targets", StringComparison.Ordinal));

    private static string ConsumerBuildText() => string.Join("\n", ConsumerBuildFiles().Select(File.ReadAllText));

    private static string ConsumerSources() => string.Join("\n", ConsumerFiles(static f => f.EndsWith(".cs", StringComparison.Ordinal)).Select(File.ReadAllText));

    private static string[] ConsumerPackageReferences() =>
        Regex.Matches(ConsumerBuildText(), @"<PackageReference\s+Include=""(?<id>[^""]+)""").Select(static m => m.Groups["id"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
