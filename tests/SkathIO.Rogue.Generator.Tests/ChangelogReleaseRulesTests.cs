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
/// The 2.0.0 CHANGELOG and the release-readiness checklist. What was under [Unreleased] now sits in
/// a dated "## [2.0.0]" section, a major bump from the last release, 1.2.1, with an empty
/// [Unreleased] above it and compare links in the Keep a Changelog form. Every package version,
/// dependency and target-framework claim in the 2.0.0 entry matches what the branch ships:
/// Directory.Packages.props for packages and each project file for its targets. The public API is
/// promoted, the newest benchmark run is committed, and the CHANGELOG is committed.
/// </summary>
public sealed class ChangelogReleaseRulesTests
{
    private const string Changelog = "CHANGELOG.md";
    private const string RequestDate = "2026-11-22";
    private const string Repo = "https://github.com/skathio/rogue";

    private static readonly string[] _newPackages =
    {
        "SkathIO.Rogue.Jobs",
        "SkathIO.Rogue.Jobs.Postgres",
        "SkathIO.Rogue.Jobs.SqlServer",
        "SkathIO.Rogue.Validation",
        "SkathIO.Rogue.Messaging",
        "SkathIO.Rogue.Messaging.RabbitMQ",
        "SkathIO.Rogue.Messaging.Outbox",
    };

    private static readonly string[] _netStandardDropped =
    {
        "SkathIO.Rogue.Abstractions",
        "SkathIO.Rogue",
        "SkathIO.Rogue.MediatR",
        "SkathIO.Rogue.Logging",
        "SkathIO.Rogue.Validation.FluentValidation",
    };

    // ── Section layout ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"^## \[Unreleased\]\s*$")]
    [InlineData(@"^## \[2\.0\.0\] - \d{4}-\d{2}-\d{2}\s*$")]
    public void TheHeading_AppearsExactlyOnce(string pattern)
    {
        int count = Regex.Matches(Text(), pattern, RegexOptions.Multiline).Count;

        Assert.True(count == 1, "CHANGELOG.md has " + count + " headings matching " + pattern + ", expected exactly one.");
    }

    [Fact]
    public void TheFirstReleaseHeading_IsUnreleased_AndTheNextIs200()
    {
        string[] headings = ReleaseHeadings();

        Assert.True(headings.Length >= 3, "CHANGELOG.md has too few release headings.");
        Assert.Equal("## [Unreleased]", headings[0]);
        Assert.StartsWith("## [2.0.0] - ", headings[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseBelow200_IsTheLastRelease121()
    {
        Assert.StartsWith("## [1.2.1] - ", ReleaseHeadings()[2], StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnreleasedSection_IsEmpty()
    {
        Match unreleased = Regex.Match(Text(), @"(?ms)^## \[Unreleased\]\s*$(?<body>.*?)(?=^## \[)");
        Assert.True(unreleased.Success, "CHANGELOG.md has no [Unreleased] section followed by a release.");

        string body = unreleased.Groups["body"].Value;
        Assert.True(body.Trim().Length == 0, "[Unreleased] is not empty:\n" + body);
    }

    // ── Release date ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheReleaseDate_IsARealDate_NotBeforeTheRequest()
    {
        DateOnly date = ReleaseDate();

        Assert.True(
            date >= DateOnly.ParseExact(RequestDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            "The 2.0.0 date " + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " is before the release was prepared.");
    }

    [Fact]
    public void TheReleaseDate_IsNotAfterTheCommitThatHoldsIt()
    {
        string committed = Git("log", "-1", "--format=%cs", "--", Changelog).Trim();
        Assert.False(committed.Length == 0, "CHANGELOG.md has no commit.");

        DateOnly commitDate = DateOnly.ParseExact(committed, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.True(ReleaseDate() <= commitDate, "The 2.0.0 date is after the commit date " + committed + ".");
    }

    // ── Compare links ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("[Unreleased]: " + Repo + "/compare/v2.0.0...HEAD")]
    [InlineData("[2.0.0]: " + Repo + "/compare/v1.2.1...v2.0.0")]
    [InlineData("[1.2.1]: " + Repo + "/compare/v1.2.0...v1.2.1")]
    public void TheCompareLink_IsPresent(string link)
    {
        string[] lines = Text().Split('\n').Select(static l => l.TrimEnd('\r', ' ')).ToArray();

        Assert.Contains(link, lines);
    }

    // ── The moved content ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("### Added")]
    [InlineData("### Removed")]
    [InlineData("### Changed")]
    public void The200Section_HoldsTheSubsection(string heading)
    {
        string[] lines = Release().Split('\n').Select(static l => l.TrimEnd('\r', ' ')).ToArray();

        Assert.Contains(heading, lines);
    }

    [Fact]
    public void The200Section_NamesEveryNewPackage()
    {
        string release = Release();
        string[] missing = _newPackages.Where(p => !release.Contains("New package: `" + p + "`", StringComparison.Ordinal)).ToArray();

        Assert.True(missing.Length == 0, "The 2.0.0 section has no \"New package\" entry for: " + string.Join(", ", missing));
    }

    [Fact]
    public void The200Section_RecordsTheNetStandardDropAsBreaking()
    {
        Assert.Matches(@"Breaking: `netstandard2\.0` is no longer a target", Release());
    }

    [Fact]
    public void The200Section_CarriesNoPlanningIds()
    {
        string release = Release();
        string[] found = Regex.Matches(release, @"\b(?:PD|NFR|FR|D)-[A-Z]*-?\d+\b|\.somi\b").Select(static m => m.Value).ToArray();

        Assert.True(found.Length == 0, "The 2.0.0 section cites local planning ids: " + string.Join(", ", found));
    }

    // ── Claims against the branch ─────────────────────────────────────────────────────

    [Fact]
    public void EveryVersionStatedForAPinnedPackage_MatchesItsPin()
    {
        string release = Normalize(Release());
        var wrong = new List<string>();
        foreach (KeyValuePair<string, string> pin in Pins())
        {
            string pattern = IdPattern(pin.Key) + @"\s*\(?\s*v?(?<version>\d+(?:\.\d+){0,3})\b";
            foreach (Match m in Regex.Matches(release, pattern))
            {
                if (!IsVersionPrefix(m.Groups["version"].Value, pin.Value))
                {
                    wrong.Add(pin.Key + " " + m.Groups["version"].Value + " (pinned " + pin.Value + ")");
                }
            }
        }

        Assert.True(wrong.Count == 0, "The 2.0.0 section states versions the branch does not pin:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void EveryNewVersionInAnUpFromClaim_MatchesThePinOfThePackageItFollows()
    {
        string release = Normalize(Release());
        Dictionary<string, string> pins = Pins();
        var wrong = new List<string>();
        foreach (Match m in Regex.Matches(release, @"\((?<version>\d+\.\d+\.\d+), up from"))
        {
            string before = release.Substring(0, m.Index);
            string? package = pins.Keys
                .Select(id => (Id: id, At: Regex.Matches(before, IdPattern(id)).Select(static x => x.Index).DefaultIfEmpty(-1).Max()))
                .Where(static x => x.At >= 0)
                .OrderByDescending(static x => x.At)
                .Select(static x => x.Id)
                .FirstOrDefault();
            Assert.True(package is not null, "No pinned package precedes the claim \"" + m.Value + "\".");
            if (pins[package] != m.Groups["version"].Value)
            {
                wrong.Add(package + " " + m.Groups["version"].Value + " (pinned " + pins[package] + ")");
            }
        }

        Assert.True(wrong.Count == 0, "Up-from claims that do not match the pins:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void EveryPackageNamedAsADependency_IsPinned()
    {
        Dictionary<string, string> pins = Pins();
        string[] unpinned = Regex.Matches(Normalize(Release()), @"depends\s+on\s+`?(?<id>[A-Z][\w.]*\w)`?")
            .Select(static m => m.Groups["id"].Value)
            .Where(id => !pins.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(unpinned.Length == 0, "The 2.0.0 section names dependencies Directory.Packages.props does not pin: " + string.Join(", ", unpinned));
    }

    [Fact]
    public void EveryNewPackageEntry_StatesTheTargetsItsProjectBuilds()
    {
        string release = Normalize(Release());
        var wrong = new List<string>();
        foreach (string package in _newPackages)
        {
            Match entry = Regex.Match(release, @"(?ms)^- New package: `" + Regex.Escape(package) + @"`(?<body>.*?)(?=^- |^### |\z)");
            Assert.True(entry.Success, "No entry for " + package + ".");
            string[] stated = Regex.Matches(entry.Groups["body"].Value, @"`(?<tfm>net[\w.]+)`").Select(static m => m.Groups["tfm"].Value).Distinct().OrderBy(static t => t, StringComparer.Ordinal).ToArray();
            string[] built = TargetFrameworks(package);
            if (!stated.SequenceEqual(built))
            {
                wrong.Add(package + ": states " + string.Join(";", stated) + ", builds " + string.Join(";", built));
            }
        }

        Assert.True(wrong.Count == 0, "Target claims that do not match the project files:\n" + string.Join("\n", wrong));
    }

    [Theory]
    [MemberData(nameof(NetStandardDropped))]
    public void APackageTheNetStandardDropNames_TargetsOnlyNet8AndNet10(string package)
    {
        Assert.Contains("`" + package + "`", Section(Release(), "### Removed"), StringComparison.Ordinal);
        Assert.Equal(new[] { "net10.0", "net8.0" }, TargetFrameworks(package));
    }

    public static TheoryData<string> NetStandardDropped()
    {
        var data = new TheoryData<string>();
        foreach (string package in _netStandardDropped)
        {
            data.Add(package);
        }
        return data;
    }

    // ── Release readiness ─────────────────────────────────────────────────────────────

    [Fact]
    public void EveryPackableProject_HasNoUnshippedPublicApi()
    {
        string[] pending = RepoFiles.PackableProjects()
            .Select(static p => Path.Combine(Path.GetDirectoryName(p)!, "PublicAPI.Unshipped.txt").Replace('\\', '/'))
            .Where(static f => RepoFiles.Read(f).Split('\n').Select(static l => l.Trim()).Any(static l => l.Length > 0 && l != "#nullable enable"))
            .ToArray();

        Assert.True(pending.Length == 0, "Unshipped public API is left in: " + string.Join(", ", pending));
    }

    [Fact]
    public void TheNewestBenchmarkRun_IsCommittedAndUnignored()
    {
        string newest = Git("ls-files", "--", "bench/results")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static p => p.Split('/'))
            .Where(static parts => parts.Length > 3 && Regex.IsMatch(parts[2], @"^\d{4}-\d{2}-\d{2}-[0-9a-f]{7,}"))
            .Select(static parts => parts[2])
            .OrderByDescending(static d => d, StringComparer.Ordinal)
            .First();

        Assert.True(string.CompareOrdinal(newest, "2026-11-10") > 0, "The newest committed run " + newest + " predates the 1.2.1 release.");
        Assert.Contains("!bench/results/" + newest + "/", RepoFiles.Read(".gitignore").Split('\n').Select(static l => l.Trim()));
    }

    [Fact]
    public void TheChangelog_IsCommitted()
    {
        string status = Git("status", "--porcelain", "--", Changelog);

        Assert.True(status.Length == 0, Changelog + " has uncommitted changes:\n" + status);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    private static string Text() => RepoFiles.Read(Changelog).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Normalize(string text) => text.Replace("**", string.Empty, StringComparison.Ordinal);

    private static string[] ReleaseHeadings() =>
        Text().Split('\n').Select(static l => l.TrimEnd()).Where(static l => l.StartsWith("## [", StringComparison.Ordinal)).ToArray();

    private static string Release()
    {
        Match release = Regex.Match(Text(), @"(?ms)^## \[2\.0\.0\][^\n]*\n(?<body>.*?)(?=^## \[|^\[Unreleased\]:|\z)");
        Assert.True(release.Success, "CHANGELOG.md has no [2.0.0] section.");
        return release.Groups["body"].Value;
    }

    private static string Section(string text, string heading)
    {
        Match section = Regex.Match(text, @"(?ms)^" + Regex.Escape(heading) + @"\s*$(?<body>.*?)(?=^### |\z)");
        Assert.True(section.Success, "The 2.0.0 section has no " + heading + " subsection.");
        return section.Groups["body"].Value;
    }

    private static DateOnly ReleaseDate()
    {
        Match heading = Regex.Match(Text(), @"(?m)^## \[2\.0\.0\] - (?<date>\S+)\s*$");
        Assert.True(heading.Success, "CHANGELOG.md has no dated [2.0.0] heading.");
        bool parsed = DateOnly.TryParseExact(heading.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date);
        Assert.True(parsed, "\"" + heading.Groups["date"].Value + "\" is not a YYYY-MM-DD date.");
        return date;
    }

    private static Dictionary<string, string> Pins() =>
        Regex.Matches(RepoFiles.Read("Directory.Packages.props"), @"<PackageVersion\s+Include=""(?<id>[^""]+)""\s+Version=""(?<version>[^""]+)""")
            .ToDictionary(static m => m.Groups["id"].Value, static m => m.Groups["version"].Value, StringComparer.Ordinal);

    // A package id as prose writes it, with or without backticks, and not part of a longer id.
    private static string IdPattern(string id) => @"(?<![\w.])`?" + Regex.Escape(id) + @"`?(?!\.?\w)";

    private static bool IsVersionPrefix(string stated, string pinned)
    {
        string[] s = stated.Split('.');
        string[] p = pinned.Split('.');
        return s.Length <= p.Length && s.Select((part, i) => part == p[i]).All(static same => same);
    }

    private static string[] TargetFrameworks(string package)
    {
        string project = RepoFiles.Read("src/" + package + "/" + package + ".csproj");
        Match tfms = Regex.Match(project, @"<TargetFrameworks?>(?<value>[^<]+)</TargetFrameworks?>");
        Assert.True(tfms.Success, package + " has no target framework.");
        return tfms.Groups["value"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(static t => t, StringComparer.Ordinal)
            .ToArray();
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
}
