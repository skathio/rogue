using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// Release wiring for the jobs and messaging families. The license check and the allow-list header
/// cover every packable project, and every package in their restore graphs is allow-listed with the
/// license nuget.org reports. Bench-only and test-only packages never reach a packable project. CI
/// publishes a jobs and messaging sample with native AOT. The governance doc, CLAUDE.md, the README,
/// the roadmap, the site and the CHANGELOG's newest entry describe the new packages.
/// </summary>
public sealed class ReleaseWiringRulesTests
{
    private const string AllowListPath = ".github/allowed-packages.txt";
    private const string CiPath = ".github/workflows/ci.yml";
    private const string JobsPackage = "SkathIO.Rogue.Jobs";
    private const string MessagingPackage = "SkathIO.Rogue.Messaging";

    private static readonly string[] _numberWords =
    {
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
    };

    public static TheoryData<string> PackableProjects()
    {
        var data = new TheoryData<string>();
        foreach (string project in RepoFiles.PackableProjects())
        {
            data.Add(project);
        }
        return data;
    }

    public static TheoryData<string> Families() => new TheoryData<string> { JobsPackage, MessagingPackage };

    [Fact]
    public void ThereAreThePackableProjectsTheExpansionAdds()
    {
        string[] ids = RepoFiles.PackableProjects().Select(PackageId).ToArray();

        Assert.Contains(JobsPackage, ids);
        Assert.Contains(MessagingPackage, ids);
    }

    [Theory]
    [MemberData(nameof(PackableProjects))]
    public void LicenseCheck_ListsEveryPackableProject(string project)
    {
        Match list = Regex.Match(RepoFiles.Read(CiPath), @"packable_projects=\((?<list>.*?)\)", RegexOptions.Singleline);

        Assert.True(list.Success, "ci.yml has no packable_projects list.");
        Assert.Contains("\"" + project + "\"", list.Groups["list"].Value, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PackableProjects))]
    public void AllowListHeader_NamesEveryPackableProject(string project)
    {
        string header = string.Join("\n", File.ReadLines(Path.Combine(RepoFiles.Root, AllowListPath)).TakeWhile(static l => l.StartsWith('#')));

        Assert.Contains(project, header, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PackableProjects))]
    public void EveryPackageInAPackableRestoreGraph_IsAllowListed_WithItsLicenseAnnotation(string project)
    {
        Dictionary<string, string> allowed = AllowList();

        string[] unannotated = RestoreGraph(project)
            .Where(id => !allowed.TryGetValue(id, out string? note) || !IsLicenseNote(id, note))
            .ToArray();

        Assert.True(unannotated.Length == 0, project + " pulls in packages not allow-listed with a '# <id> — <license> — verified <date>' note: " + string.Join(", ", unannotated));
    }

    [Theory]
    [MemberData(nameof(PackableProjects))]
    public void BenchOnlyAndTestOnlyPackages_NeverReachAPackableProject(string project)
    {
        string[] leaks = RestoreGraph(project).Where(IsBenchOrTestOnly).ToArray();

        Assert.True(leaks.Length == 0, project + " pulls in bench-only or test-only packages: " + string.Join(", ", leaks));
    }

    [Fact]
    public void AllowList_HoldsNoBenchOnlyOrTestOnlyPackage()
    {
        string[] listed = AllowList().Keys.Where(IsBenchOrTestOnly).ToArray();

        Assert.True(listed.Length == 0, "The allow-list would let these bench-only or test-only packages ship: " + string.Join(", ", listed));
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Ci_PublishesAnAotSample_ThatUsesTheFamily(string family)
    {
        string[] samples = AotSamples().Where(s => ReferencesPackage(RepoFiles.Read(s), family)).ToArray();

        Assert.True(samples.Length > 0, "No samples/ project with PublishAot references " + family + ".");
        Assert.Contains(samples, static s => CiJobs().Any(j => j.Contains(s, StringComparison.Ordinal) && j.Contains("dotnet publish", StringComparison.Ordinal)));
    }

    [Fact]
    public void JobsAndMessagingAotSamples_SuppressNoTrimOrAotWarning()
    {
        string[] samples = FamilySamples();

        Assert.NotEmpty(samples);
        Assert.All(samples, static s => Assert.DoesNotMatch(@"<(NoWarn|WarningsNotAsErrors)>[^<]*\bIL\d{4}", RepoFiles.Read(s)));
        Assert.All(samples, static s => Assert.DoesNotMatch(@"<(TreatWarningsAsErrors|IsAotCompatible)>\s*false|<SuppressTrimAnalysisWarnings>\s*true", RepoFiles.Read(s)));
    }

    [Fact]
    public void JobsAndMessagingAotSamples_KeepTheProjectConventions()
    {
        string[] samples = FamilySamples();

        Assert.NotEmpty(samples);
        Assert.All(samples, static s => Assert.DoesNotMatch(@"<PackageReference\s+Include=""[^""]+""[^>]*\bVersion\s*=", RepoFiles.Read(s)));
        Assert.All(samples, static s => Assert.DoesNotMatch(@"<Nullable>\s*disable|<ImplicitUsings>\s*enable", RepoFiles.Read(s)));
        Assert.All(samples, static s => Assert.DoesNotMatch(@"<IsPackable>\s*true", RepoFiles.Read(s)));
    }

    [Fact]
    public void ClaudeMd_AotCheck_NamesEverySampleCiPublishes()
    {
        string claude = RepoFiles.Read("CLAUDE.md");
        string[] samples = FamilySamples();

        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.Contains(s, claude, StringComparison.Ordinal));
    }

    [Fact]
    public void Ci_PinsHashiraByCommitSha_WithoutLibraryRef()
    {
        string ci = RepoFiles.Read(CiPath);
        Match uses = Regex.Match(ci, @"uses:\s*skathio/hashira/[^@\s]+@(?<ref>[^\s#]+)");

        Assert.True(uses.Success, "ci.yml lost its hashira pin.");
        Assert.Matches("^[0-9a-f]{40}$", uses.Groups["ref"].Value);
        // hashira v3 checks its actions out at its own commit and fails a run that passes library_ref.
        Assert.DoesNotMatch(@"(?m)^\s*library_ref:", ci);
    }

    [Theory]
    [MemberData(nameof(PackableProjects))]
    public void Governance_NamesEveryPackableProject(string project)
    {
        Assert.Matches(IdPattern(PackageId(project)), RepoFiles.Read("docs/governance.md"));
    }

    [Fact]
    public void Governance_DocumentsTheSniRuntimeLicenseException()
    {
        Assert.Contains("Microsoft.Data.SqlClient.SNI.runtime", RepoFiles.Read("docs/governance.md"), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PackableProjects))]
    public void ReadmePackagesTable_HasARowForEveryPackableProject(string project)
    {
        Assert.Matches(@"(?m)^\|\s*`" + Regex.Escape(PackageId(project)) + @"`\s*\|", RepoFiles.Read("README.md"));
    }

    [Fact]
    public void ReadmeRequirements_NoLongerOfferNetStandard()
    {
        Match requirements = Regex.Match(RepoFiles.Read("README.md"), @"(?ms)^## Requirements\s*$(?<body>.*?)(?=^## |\z)");

        Assert.True(requirements.Success, "README.md has no Requirements section.");
        Assert.DoesNotMatch(@"(?i)\.NET\s*Standard|netstandard", requirements.Groups["body"].Value);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void ClaudeMd_NamesTheFamily(string family)
    {
        Assert.Contains(family, RepoFiles.Read("CLAUDE.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeMd_PackageCount_MatchesThePackableProjects()
    {
        Match count = Regex.Match(RepoFiles.Read("CLAUDE.md"), @"(?i)published to NuGet as (?<n>\w+) packages");

        Assert.True(count.Success, "CLAUDE.md no longer says how many packages it publishes.");
        Assert.Equal(RepoFiles.PackableProjects().Length, ParseCount(count.Groups["n"].Value));
    }

    [Theory]
    [InlineData(@"(?i)\bjobs?\b", "jobs")]
    [InlineData(@"(?i)\bmessag(e|es|ing)\b", "messaging")]
    public void Roadmap_CoversTheFamily(string pattern, string family)
    {
        Assert.True(Regex.IsMatch(RepoFiles.Read("docs/roadmap.md"), pattern), "docs/roadmap.md says nothing about " + family + ".");
    }

    [Theory]
    [InlineData(@"(?i)\bjobs?\b", "jobs")]
    [InlineData(@"(?i)\bmessag(e|es|ing)\b", "messaging")]
    public void Site_ShowsTheFamily(string pattern, string family)
    {
        Assert.True(Regex.IsMatch(VisibleText(RepoFiles.Read("site/index.html")), pattern), "site/index.html shows nothing about " + family + ".");
    }

    [Fact]
    public void Site_KeepsItsTitle()
    {
        Assert.Matches(@"<title>\s*Rogue\s*</title>", RepoFiles.Read("site/index.html"));
    }

    [Theory]
    [MemberData(nameof(PackableProjects))]
    public void ChangelogUnreleased_NamesEveryPackageNoReleaseNamesYet(string project)
    {
        string id = PackageId(project);
        (string unreleased, string released) = ChangelogParts();

        Assert.True(Regex.IsMatch(released, IdPattern(id)) || Regex.IsMatch(unreleased, IdPattern(id)), id + " is new, yet CHANGELOG.md [Unreleased] does not name it.");
    }

    [Fact]
    public void ChangelogNewestEntry_CarriesNoPlanningIds()
    {
        string newest = NewestChangelogEntry();

        Assert.NotEmpty(newest.Trim());
        Assert.DoesNotMatch(@"\.somi\b|\b(PD|NFR|AC)-[A-Z0-9-]*\d|\bM\d{1,2}\b", newest);
    }

    private static string PackageId(string project) => Path.GetFileNameWithoutExtension(project);

    private static string IdPattern(string id) => @"(?<![\w.])" + Regex.Escape(id) + @"(?!\w|\.\w)";

    private static bool IsBenchOrTestOnly(string id) =>
        id.StartsWith("Hangfire", StringComparison.OrdinalIgnoreCase)
        || id.Equals("Newtonsoft.Json", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("Testcontainers", StringComparison.OrdinalIgnoreCase);

    private static bool ReferencesPackage(string project, string id) =>
        Regex.IsMatch(project, @"Include=""(?:[^""]*[\\/])?" + Regex.Escape(id) + @"(?:\.csproj)?""");

    private static int ParseCount(string word) =>
        int.TryParse(word, out int n) ? n : Array.FindIndex(_numberWords, w => w.Equals(word, StringComparison.OrdinalIgnoreCase));

    private static string VisibleText(string html)
    {
        string text = Regex.Replace(html, @"(?is)<(script|style)\b.*?</\1>|<!--.*?-->", " ");
        return Regex.Replace(text, @"<[^>]*>", " ");
    }

    // The [Unreleased] section, and every released section after it.
    private static (string Unreleased, string Released) ChangelogParts()
    {
        string changelog = RepoFiles.Read("CHANGELOG.md");
        Match unreleased = Regex.Match(changelog, @"(?ms)^## \[Unreleased\]\s*$(?<body>.*?)(?=^## \[)");
        Assert.True(unreleased.Success, "CHANGELOG.md has no [Unreleased] section followed by a release.");
        return (unreleased.Groups["body"].Value, changelog.Substring(unreleased.Index + unreleased.Length));
    }

    // The first section from [Unreleased] down that has content: [Unreleased] until a release moves
    // it into a dated section, then that release.
    private static string NewestChangelogEntry()
    {
        string changelog = RepoFiles.Read("CHANGELOG.md");
        string? newest = Regex.Matches(changelog, @"(?ms)^## \[[^\n]*\n(?<body>.*?)(?=^## \[|^\[Unreleased\]:)")
            .Select(static m => m.Groups["body"].Value)
            .FirstOrDefault(static body => body.Trim().Length > 0);
        Assert.True(newest is not null, "CHANGELOG.md has no section with content.");
        return newest;
    }

    private static bool IsBuildOutput(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal) || normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    // Projects under samples/ that publish with native AOT, as "samples/X/X.csproj".
    private static string[] AotSamples() =>
        Directory.GetFiles(Path.Combine(RepoFiles.Root, "samples"), "*.csproj", SearchOption.AllDirectories)
            .Where(static p => !IsBuildOutput(p))
            .Where(static p => Regex.IsMatch(File.ReadAllText(p), @"<PublishAot>\s*true\s*</PublishAot>", RegexOptions.IgnoreCase))
            .Select(static p => Path.GetRelativePath(RepoFiles.Root, p).Replace('\\', '/'))
            .ToArray();

    private static string[] FamilySamples() =>
        AotSamples().Where(static s => ReferencesPackage(RepoFiles.Read(s), JobsPackage) || ReferencesPackage(RepoFiles.Read(s), MessagingPackage)).ToArray();

    // The text of each job in ci.yml, split at the two-space-indented job keys.
    private static string[] CiJobs()
    {
        string ci = RepoFiles.Read(CiPath);
        int jobs = ci.IndexOf("\njobs:", StringComparison.Ordinal);
        Assert.True(jobs >= 0, "ci.yml has no jobs.");
        return Regex.Split(ci.Substring(jobs), @"(?m)^(?=  [A-Za-z][\w-]*:\s*$)");
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
