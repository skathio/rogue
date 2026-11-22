using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The .NET 8 test job. ci.yml has a job that installs the .NET 8 runtime next to the .NET 10 SDK and
/// runs tests under net8.0, so the net8.0 builds of the runtime packages are tested on every pull
/// request. It runs in parallel to the other jobs, pins each action by commit SHA as the other jobs do,
/// and fails the build when a test fails.
/// </summary>
public sealed class Net8CiJobRulesTests
{
    private const string CiPath = ".github/workflows/ci.yml";
    private const string Net8 = "net8.0";
    private const string DefaultSolution = "SkathIO.Rogue.sln";

    // ── The job installs .NET 8 next to the SDK ──────────────────────────────────────

    [Fact]
    public void Ci_HasAJob_ThatInstallsDotNet8()
    {
        Assert.True(
            Net8Jobs().Length > 0,
            "No job in ci.yml installs .NET 8 with actions/setup-dotnet (a dotnet-version of 8.x).");
    }

    [Fact]
    public void Net8Job_InstallsTheDotNet10Sdk_AlongsideDotNet8()
    {
        Assert.All(Net8TestJobs(), static job =>
        {
            int[] majors = SetupDotnetMajors(job.Text).ToArray();
            Assert.True(majors.Contains(8), job.Name + " does not install .NET 8.");
            Assert.True(majors.Contains(10), job.Name + " installs .NET 8 but not the .NET 10 SDK the solution builds with.");
        });
    }

    // ── The job runs tests under net8.0 ──────────────────────────────────────────────

    [Fact]
    public void Net8Job_RunsDotnetTest()
    {
        Assert.True(
            Net8TestJobs().Length > 0,
            "The job that installs .NET 8 runs no `dotnet test`, in its steps or in a script they run.");
    }

    [Fact]
    public void Net8Job_SelectsNoFrameworkOtherThanNet8()
    {
        Assert.All(Net8TestJobs(), static job =>
        {
            foreach (string command in TestCommands(job))
            {
                string? framework = Framework(command);
                Assert.True(
                    framework is null || framework == Net8,
                    job.Name + " runs tests under " + framework + " instead of net8.0: " + command);
            }
        });
    }

    [Fact]
    public void Net8Job_RunsAtLeastOneTestProject_UnderNet8()
    {
        Assert.All(Net8TestJobs(), static job =>
        {
            string[] underNet8 = TestCommands(job)
                .SelectMany(static c => TestedProjects(c))
                .Where(static p => TargetFrameworks(p).Contains(Net8, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            Assert.True(
                underNet8.Length > 0,
                job.Name + " runs `dotnet test`, but none of the test projects it runs targets net8.0.");
        });
    }

    [Fact]
    public void Net8Job_SelectsNet8_OnlyForTestProjectsThatTargetIt()
    {
        Assert.All(Net8TestJobs(), static job =>
        {
            foreach (string command in TestCommands(job).Where(static c => Framework(c) == Net8))
            {
                string[] missing = TestedProjects(command)
                    .Where(static p => !TargetFrameworks(p).Contains(Net8, StringComparer.OrdinalIgnoreCase))
                    .ToArray();

                Assert.True(
                    missing.Length == 0,
                    job.Name + " runs `" + command.Trim() + "`, but these test projects do not target net8.0: " + string.Join(", ", missing));
            }
        });
    }

    // ── Pinned, parallel and a real gate ─────────────────────────────────────────────

    [Fact]
    public void Net8Job_UsesActions()
    {
        Assert.All(Net8TestJobs(), static job => Assert.NotEmpty(Uses(job.Text)));
    }

    [Fact]
    public void Net8Job_PinsEveryActionByCommitSha()
    {
        Assert.All(Net8TestJobs(), static job =>
        {
            string[] unpinned = Uses(job.Text).Where(static u => !Regex.IsMatch(u, @"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$")).ToArray();
            Assert.True(unpinned.Length == 0, job.Name + " uses actions not pinned by a 40-character commit SHA: " + string.Join(", ", unpinned));
        });
    }

    [Fact]
    public void Net8Job_DependsOnNoOtherJob()
    {
        Assert.All(Net8TestJobs(), static job =>
            Assert.False(Regex.IsMatch(job.Text, @"(?m)^    needs:"), job.Name + " has `needs:`, so it does not run in parallel to the other jobs."));
    }

    [Fact]
    public void Net8Job_FailsTheBuild_WhenATestFails()
    {
        Assert.All(Net8TestJobs(), static job =>
            Assert.False(
                Regex.IsMatch(job.Text, @"(?m)^\s*continue-on-error:\s*true\b"),
                job.Name + " sets continue-on-error, so a failing test would not fail the build."));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private sealed record CiJob(string Name, string Text);

    // The jobs in ci.yml, split at the two-space-indented job keys.
    private static CiJob[] CiJobs()
    {
        string ci = RepoFiles.Read(CiPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        int jobs = ci.IndexOf("\njobs:", StringComparison.Ordinal);
        Assert.True(jobs >= 0, "ci.yml has no jobs.");
        return Regex.Split(ci.Substring(jobs), @"(?m)^(?=  [A-Za-z][\w-]*:\s*$)")
            .Skip(1)
            .Select(static t => new CiJob(Regex.Match(t, @"^  (?<name>[A-Za-z][\w-]*):").Groups["name"].Value, t))
            .ToArray();
    }

    private static CiJob[] Net8Jobs() => CiJobs().Where(static j => SetupDotnetMajors(j.Text).Contains(8)).ToArray();

    private static CiJob[] Net8TestJobs()
    {
        CiJob[] jobs = Net8Jobs().Where(static j => TestCommands(j).Any()).ToArray();
        Assert.True(jobs.Length > 0, "No job in ci.yml both installs .NET 8 and runs `dotnet test`.");
        return jobs;
    }

    // The major versions each actions/setup-dotnet step of a job installs.
    private static IEnumerable<int> SetupDotnetMajors(string job)
    {
        foreach (string step in Regex.Split(job, @"(?m)^(?=\s*- )"))
        {
            if (!step.Contains("actions/setup-dotnet@", StringComparison.Ordinal))
            {
                continue;
            }
            int at = step.IndexOf("dotnet-version:", StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }
            foreach (Match m in Regex.Matches(step.Substring(at), @"(?<![\w.])(?<major>\d+)\.(x|\d+)"))
            {
                yield return int.Parse(m.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    private static string[] Uses(string job) =>
        Regex.Matches(job, @"(?m)^\s*(-\s+)?uses:\s*['""]?(?<ref>[^\s'""#]+)")
            .Select(static m => m.Groups["ref"].Value)
            .ToArray();

    // Each `dotnet test` command line of a job, with those of the repository scripts it runs.
    private static IEnumerable<string> TestCommands(CiJob job) =>
        Regex.Matches(WithScripts(job.Text, 2), @"dotnet\s+test\b[^\n]*").Select(static m => m.Value);

    private static string? Framework(string command)
    {
        Match m = Regex.Match(command, @"(?:^|\s)(-f|--framework)(\s+|=|:)['""]?(?<tfm>[\w.-]+)");
        return m.Success ? m.Groups["tfm"].Value : null;
    }

    // The test projects a `dotnet test` command runs, as paths relative to the repository root.
    private static IEnumerable<string> TestedProjects(string command)
    {
        string[] targets = Regex.Matches(command, @"(?<path>[\w./\\-]+\.(sln|slnx|csproj))\b")
            .Select(static m => m.Groups["path"].Value.Replace('\\', '/').TrimStart('.', '/'))
            .ToArray();
        if (targets.Length == 0)
        {
            targets = new[] { DefaultSolution };
        }

        foreach (string target in targets)
        {
            IEnumerable<string> projects = target.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? new[] { target }
                : SolutionProjects(target);
            foreach (string project in projects.Where(IsTestProject))
            {
                yield return project;
            }
        }
    }

    private static IEnumerable<string> SolutionProjects(string solution)
    {
        string path = Path.Combine(RepoFiles.Root, solution);
        Assert.True(File.Exists(path), "The `dotnet test` target " + solution + " does not exist.");
        string directory = Path.GetDirectoryName(solution) ?? string.Empty;
        return Regex.Matches(File.ReadAllText(path), @"[""'](?<p>[^""']+\.csproj)[""']")
            .Select(m => Path.Combine(directory, m.Groups["p"].Value.Replace('\\', '/')).Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal);
    }

    private static bool IsTestProject(string project)
    {
        string path = Path.Combine(RepoFiles.Root, project);
        return File.Exists(path)
            && Regex.IsMatch(File.ReadAllText(path), @"<IsTestProject>\s*true\s*</IsTestProject>|Include=""Microsoft\.NET\.Test\.Sdk""", RegexOptions.IgnoreCase);
    }

    private static string[] TargetFrameworks(string project)
    {
        string text = File.ReadAllText(Path.Combine(RepoFiles.Root, project));
        return Regex.Matches(text, @"<TargetFrameworks?>(?<v>[^<]*)</TargetFrameworks?>")
            .SelectMany(static m => m.Groups["v"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
    }

    // A job's text with the text of each repository script it runs, the given number of levels deep.
    private static string WithScripts(string text, int depth)
    {
        if (depth == 0)
        {
            return text;
        }
        IEnumerable<string> scripts = Regex.Matches(text, @"(?<![\w.-])(\./)?(?<path>[\w.-]+(/[\w.-]+)*\.(sh|ps1|py|mjs|js))\b")
            .Select(static m => Path.Combine(RepoFiles.Root, m.Groups["path"].Value))
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal);
        return text + "\n" + string.Join("\n", scripts.Select(s => WithScripts(File.ReadAllText(s), depth - 1)));
    }
}
