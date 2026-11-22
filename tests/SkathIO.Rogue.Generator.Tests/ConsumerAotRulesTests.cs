using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The native AOT scenarios of the end-to-end consumer suite: the consumer job in ci.yml publishes
/// samples/SkathIO.Rogue.Aot.Sample and samples/SkathIO.Rogue.Jobs.Messaging.Aot.Sample natively
/// against the packed packages, restored by package reference as a real app would, never by project
/// reference, and runs both binaries, failing on any warning or a non-zero exit code. The AOT projects
/// keep the project's conventions and bring in no package the maintainer has not license-checked.
/// </summary>
public sealed class ConsumerAotRulesTests
{
    private const string CiPath = ".github/workflows/ci.yml";
    private const string ConsumerDirectory = "tests/consumer";
    private const string MainSolution = "SkathIO.Rogue.sln";
    private const string AotInProject = @"<PublishAot>\s*true\s*</PublishAot>";
    private const string PublishInvocation = @"\bdotnet\s+publish\b|""publish""|[-/]t(arget)?:Publish\b";
    private const string RuntimeIdentifier = @"(?<![\w-])(-r|--runtime)(?=[\s=""])|\bRuntimeIdentifier\b";
    private const string PackageReference = @"<PackageReference\s+Include=""+(?<id>[^""]+)""+";

    private static readonly string[] _skippedDirectories = { "bin", "obj", "TestResults" };
    private static readonly string[] _consumerExtensions = { ".cs", ".sh", ".csproj", ".props", ".targets" };

    public static TheoryData<string> Samples() =>
        new TheoryData<string>("SkathIO.Rogue.Aot.Sample", "SkathIO.Rogue.Jobs.Messaging.Aot.Sample");

    [Theory]
    [MemberData(nameof(Samples))]
    public void EachSample_HasANativeAotProject_InTheConsumerSuite(string sample)
    {
        Assert.True(UnitsFor(sample).Length > 0, "No project under " + ConsumerDirectory + " sets PublishAot to true and builds " + sample + ".");
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void EachSamplesAotProject_RestoresTheSamplesPackages_ByPackageReference(string sample)
    {
        string[] packages = SamplePackages(sample);

        Assert.NotEmpty(packages);
        Assert.All(UnitsFor(sample), unit =>
        {
            string[] missing = packages.Where(p => !PackageReferences(unit).Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray();
            Assert.True(missing.Length == 0, unit.Name + " publishes " + sample + " without a package reference to: " + string.Join(", ", missing));
        });
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void EachSamplesAotProject_ReferencesNoProjectUnderSrc_NorTheSampleProject(string sample)
    {
        Assert.All(UnitsFor(sample), static unit =>
        {
            string[] projects = Regex.Matches(unit.Text, @"<ProjectReference\s+Include=""+(?<path>[^""]+)""+")
                .Select(static m => m.Groups["path"].Value.Replace('\\', '/'))
                .Where(static p => Regex.IsMatch(p, @"(^|/)(src|samples)/"))
                .ToArray();
            Assert.True(projects.Length == 0, unit.Name + " restores the packages, yet references these projects: " + string.Join(", ", projects));
        });
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void EachSamplesAotProject_IsNotInTheMainSolution(string sample)
    {
        string main = RepoFiles.Read(MainSolution);

        Assert.All(UnitsFor(sample).Where(static u => u.IsProject), unit =>
            Assert.DoesNotContain(Path.GetFileName(unit.Name), main, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void EachSamplesAotProject_FailsOnAnyWarning(string sample)
    {
        const string Weakened = @"<TreatWarningsAsErrors>\s*false|TreatWarningsAsErrors=false|<(Ilc|ILLink)TreatWarningsAsErrors>\s*false|<SuppressTrimAnalysisWarnings>\s*true|<NoWarn>[^<]*\b(IL\d{4}|ROGUE\d+)";

        Assert.All(UnitsFor(sample), unit =>
        {
            Assert.True(Regex.IsMatch(unit.Text + Inherited(unit), @"<TreatWarningsAsErrors>\s*true"), unit.Name + " does not treat warnings as errors.");
            Assert.False(Regex.IsMatch(unit.Text, Weakened), unit.Name + " turns off or hides warnings of the AOT publish.");
        });
        string[] weakening = SuiteSources().Where(static s => Regex.IsMatch(s.Text, @"TreatWarningsAsErrors=false|-warnaserror-|--no-warn-as-error")).Select(static s => s.Name).ToArray();
        Assert.True(weakening.Length == 0, "These turn warnings as errors off: " + string.Join(", ", weakening));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void EachSamplesAotProject_PinsNoVersion_AndUsesOnlyLicenseCheckedPackages(string sample)
    {
        string[] pinned = PinnedPackages();

        Assert.NotEmpty(pinned);
        Assert.All(UnitsFor(sample), unit =>
        {
            Assert.DoesNotMatch(@"<PackageReference\s+Include=""+[^""]+""+[^>]*\bVersion\s*=", unit.Text);
            string[] notChecked = PackageReferences(unit).Where(id => !IsRoguePackage(id) && !pinned.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray();
            Assert.True(notChecked.Length == 0, unit.Name + " uses packages the maintainer has not license-checked: " + string.Join(", ", notChecked));
        });
    }

    [Fact]
    public void ConsumerSuite_PublishesForARuntime_AsNativeAotNeeds()
    {
        bool publishes = SuiteSources().Any(static s => Regex.IsMatch(s.Text, PublishInvocation) && Regex.IsMatch(s.Text, RuntimeIdentifier))
            || (SuiteSources().Any(static s => Regex.IsMatch(s.Text, PublishInvocation)) && AotUnits().Any(static u => Regex.IsMatch(u.Text, @"<RuntimeIdentifier>")));

        Assert.True(publishes, "Neither the consumer job nor the consumer suite publishes a project for a runtime identifier.");
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void ConsumerSuite_RunsEachSamplesBinary_AndFailsOnItsExitCode(string sample)
    {
        string[] runners = UnitsFor(sample).SelectMany(RunnersOf).Distinct(StringComparer.Ordinal).ToArray();

        Assert.True(runners.Length > 0, "Nothing in the consumer job or suite runs the native binary built for " + sample + " and fails on its exit code.");
    }

    [Fact]
    public void ConsumerJob_InstallsTheNativeAotPrerequisites()
    {
        string job = string.Join("\n", ConsumerJobs().Select(static j => j.Text + "\n" + string.Join("\n", ScriptsRunBy(j.Text, 2).Select(File.ReadAllText))));

        Assert.Matches(@"\bclang\b", job);
        Assert.Matches(@"\bzlib1g-dev\b", job);
    }

    // The packages a sample takes from src/ by project reference, not counting the analyzer references.
    private static string[] SamplePackages(string sample)
    {
        string csproj = RepoFiles.Read("samples/" + sample + "/" + sample + ".csproj");
        return Regex.Matches(csproj, @"<ProjectReference\s+Include=""(?<path>[^""]+)""(?<rest>[^>]*)>")
            .Where(static m => !m.Groups["rest"].Value.Contains("OutputItemType=\"Analyzer\"", StringComparison.Ordinal))
            .Select(static m => m.Groups["path"].Value.Replace('\\', '/'))
            .Where(static p => p.Contains("/src/", StringComparison.Ordinal))
            .Select(static p => Path.GetFileNameWithoutExtension(p))
            .ToArray();
    }

    private static string[] PinnedPackages() =>
        new[] { "Directory.Packages.props", ConsumerDirectory + "/Directory.Packages.props" }
            .Where(static f => File.Exists(Path.Combine(RepoFiles.Root, f)))
            .SelectMany(static f => Regex.Matches(RepoFiles.Read(f), @"<PackageVersion\s+Include=""(?<id>[^""]+)""").Select(static m => m.Groups["id"].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsRoguePackage(string id) =>
        id.Equals("SkathIO.Rogue", StringComparison.OrdinalIgnoreCase) || id.StartsWith("SkathIO.Rogue.", StringComparison.OrdinalIgnoreCase);

    // The build files a project under the consumer directory imports from the directories above it.
    private static string Inherited(Unit unit) =>
        !unit.IsProject ? string.Empty : string.Join("\n", Directory.GetFiles(Path.Combine(RepoFiles.Root, ConsumerDirectory), "Directory.Build.*").Select(File.ReadAllText));

    private static string[] PackageReferences(Unit unit) =>
        Regex.Matches(unit.Text + "\n" + Inherited(unit), PackageReference).Select(static m => m.Groups["id"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // The places that run a unit's binary and fail on its exit code: a ci.yml step (a step fails on
    // a non-zero exit), a script under set -e, or a test that reads the process's exit code.
    private static IEnumerable<string> RunnersOf(Unit unit)
    {
        string[] names = BinaryNames(unit);
        foreach (Source source in SuiteSources().Where(static s => !s.Name.EndsWith(".csproj", StringComparison.Ordinal) && !s.Name.EndsWith(".props", StringComparison.Ordinal)))
        {
            bool failsOnExit = source.Kind switch
            {
                SourceKind.CiJob => true,
                SourceKind.Script => Regex.IsMatch(source.Text, @"(?m)^\s*set\s+-\w*e"),
                _ => Regex.IsMatch(source.Text, @"\bExitCode\b"),
            };
            bool runs = names.Any(n => source.Text.Split('\n').Any(line => !Regex.IsMatch(line, @"\bdotnet\s+\w|(?<![\w-])(-o|--output)\s")
                && Regex.IsMatch(line, @"[""'/\\]" + Regex.Escape(n) + @"(\.exe)?[""']?\s*(\)|;|,|$)")));
            bool runsItself = !unit.IsProject && source.Name == unit.Name && Regex.IsMatch(source.Text, PublishInvocation) && Regex.IsMatch(source.Text, @"\bProcess(StartInfo)?\b");
            if (failsOnExit && (runs || runsItself))
            {
                yield return source.Name;
            }
        }
    }

    private static string[] BinaryNames(Unit unit)
    {
        var names = Regex.Matches(unit.Text, @"<AssemblyName>\s*(?<name>[^<$\s]+)\s*</AssemblyName>").Select(static m => m.Groups["name"].Value).ToList();
        if (unit.IsProject && names.Count == 0)
        {
            names.Add(Path.GetFileNameWithoutExtension(unit.Name));
        }
        return names.ToArray();
    }

    // The AOT projects the consumer suite publishes for a sample: those that name it, or else those
    // published by a command that names it.
    private static Unit[] UnitsFor(string sample)
    {
        Unit[] units = AotUnits();
        Unit[] naming = units.Where(u => u.Text.Contains(sample, StringComparison.Ordinal)).ToArray();
        if (naming.Length > 0)
        {
            return naming;
        }
        string[] lines = SuiteSources().SelectMany(static s => s.Text.Split('\n')).Where(l => l.Contains(sample, StringComparison.Ordinal)).ToArray();
        return units.Where(u => u.IsProject && lines.Any(l => l.Contains(Path.GetFileName(u.Name), StringComparison.Ordinal))).ToArray();
    }

    // Each project under the consumer directory that publishes as native AOT, and each consumer test
    // that writes such a project at run time.
    private static Unit[] AotUnits()
    {
        string[] commandLines = SuiteSources().SelectMany(static s => s.Text.Split('\n')).Where(static l => Regex.IsMatch(l, @"\bPublishAot=true\b")).ToArray();
        return ConsumerFiles()
            .Select(static f => new Unit(Path.GetRelativePath(RepoFiles.Root, f).Replace('\\', '/'), File.ReadAllText(f), f.EndsWith(".csproj", StringComparison.Ordinal)))
            .Where(u => (u.IsProject || u.Name.EndsWith(".cs", StringComparison.Ordinal))
                && (Regex.IsMatch(u.Text, AotInProject) || (u.IsProject && commandLines.Any(l => l.Contains(Path.GetFileName(u.Name), StringComparison.Ordinal)))))
            .ToArray();
    }

    private static IEnumerable<string> FilesUnder(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory).Where(static f => _consumerExtensions.Contains(Path.GetExtension(f), StringComparer.Ordinal)))
        {
            yield return file;
        }
        foreach (string sub in Directory.EnumerateDirectories(directory).Where(static d => !_skippedDirectories.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)))
        {
            foreach (string file in FilesUnder(sub))
            {
                yield return file;
            }
        }
    }

    private static string[] ConsumerFiles()
    {
        string directory = Path.Combine(RepoFiles.Root, ConsumerDirectory);
        Assert.True(Directory.Exists(directory), "There is no consumer suite at " + ConsumerDirectory + ".");
        return FilesUnder(directory).ToArray();
    }

    // The consumer job's text, the scripts it runs, and every file of the consumer suite.
    private static Source[] SuiteSources()
    {
        Source[] jobs = ConsumerJobs();
        IEnumerable<Source> scripts = jobs.SelectMany(static j => ScriptsRunBy(j.Text, 2))
            .Concat(ConsumerFiles().Where(static f => f.EndsWith(".sh", StringComparison.Ordinal)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .Select(static f => new Source(Path.GetRelativePath(RepoFiles.Root, f).Replace('\\', '/'), File.ReadAllText(f), f.EndsWith(".sh", StringComparison.Ordinal) ? SourceKind.Script : SourceKind.File));
        IEnumerable<Source> files = ConsumerFiles().Where(static f => !f.EndsWith(".sh", StringComparison.Ordinal))
            .Select(static f => new Source(Path.GetRelativePath(RepoFiles.Root, f).Replace('\\', '/'), File.ReadAllText(f), SourceKind.File));
        return jobs.Concat(scripts).Concat(files).ToArray();
    }

    // The ci.yml jobs that pack the packages, directly or through a script they run.
    private static Source[] ConsumerJobs()
    {
        string ci = RepoFiles.Read(CiPath);
        int start = ci.IndexOf("\njobs:", StringComparison.Ordinal);
        Assert.True(start >= 0, "ci.yml has no jobs.");
        Source[] jobs = Regex.Split(ci.Substring(start), @"(?m)^(?=  [A-Za-z][\w-]*:\s*$)").Skip(1)
            .Where(static j => Regex.IsMatch(j + "\n" + string.Join("\n", ScriptsRunBy(j, 2).Select(File.ReadAllText)), @"dotnet\s+pack\b"))
            .Select(static j => new Source(CiPath + ": " + j.Trim().Split('\n')[0].Trim(), j, SourceKind.CiJob))
            .ToArray();
        Assert.True(jobs.Length > 0, "No job in ci.yml packs the packages.");
        return jobs;
    }

    // The repository scripts a text runs, and the scripts those run, to the given depth.
    private static IEnumerable<string> ScriptsRunBy(string text, int depth)
    {
        if (depth == 0)
        {
            return Array.Empty<string>();
        }
        string[] scripts = Regex.Matches(text, @"(?<![\w.-])(\./)?(?<path>[\w.-]+(/[\w.-]+)*\.(sh|ps1|py|mjs|js))\b")
            .Select(static m => Path.Combine(RepoFiles.Root, m.Groups["path"].Value))
            .Where(File.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return scripts.Concat(scripts.SelectMany(s => ScriptsRunBy(File.ReadAllText(s), depth - 1))).Distinct(StringComparer.Ordinal);
    }

    private enum SourceKind
    {
        CiJob,
        Script,
        File,
    }

    private sealed record Source(string Name, string Text, SourceKind Kind);

    // A project that publishes as native AOT: a csproj on disk, or a consumer test that writes one.
    private sealed record Unit(string Name, string Text, bool IsProject);
}
