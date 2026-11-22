using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// Public API promotion for the 2.0.0 release. Every packable project keeps its pair of PublicAPI
/// files at the project root. After the release prep, no Unshipped file holds an entry beyond its
/// header: each entry moved to the Shipped file of the same project, the Shipped file keeps the
/// entries it already had, and it carries the header once.
/// </summary>
public sealed class PublicApiPromotionRulesTests
{
    private const string Header = "#nullable enable";
    private const string ShippedFile = "PublicAPI.Shipped.txt";
    private const string UnshippedFile = "PublicAPI.Unshipped.txt";

    // The packages the request names, with the number of entries it says move to Shipped, and one
    // of those entries, which must now be in Shipped word for word.
    private static readonly (string Project, int Promoted, string Entry)[] _promotedPackages =
    {
        ("SkathIO.Rogue.Jobs", 241, "abstract SkathIO.Rogue.Jobs.JobDefinition.CommandType.get -> System.Type!"),
        ("SkathIO.Rogue.Messaging", 141, "abstract SkathIO.Rogue.Messaging.MessageBinding.CanDispatch.get -> bool"),
        ("SkathIO.Rogue.Validation", 73, "const SkathIO.Rogue.Validation.GreaterThanAttribute.DefaultCode = \"greater_than\" -> string!"),
        ("SkathIO.Rogue.Messaging.RabbitMQ", 37, "const SkathIO.Rogue.Messaging.RabbitMQ.RabbitMqTransportOptions.MaxNameLength = 255 -> int"),
        ("SkathIO.Rogue.Jobs.Postgres", 32, "SkathIO.Rogue.Jobs.Postgres.IPostgresJobScheduler"),
        ("SkathIO.Rogue.Jobs.SqlServer", 27, "SkathIO.Rogue.Jobs.SqlServer.SqlServerJobsOptions"),
        ("SkathIO.Rogue.Messaging.Outbox", 9, "SkathIO.Rogue.Messaging.Outbox.IMessageOutbox"),
    };

    // The packages that shipped before this release, with the number of Shipped entries they had
    // already. Moving entries in must not lose any of them.
    private static readonly (string Project, int Shipped)[] _alreadyShippedPackages =
    {
        ("SkathIO.Rogue.Abstractions", 80),
        ("SkathIO.Rogue", 77),
        ("SkathIO.Rogue.MediatR", 37),
        ("SkathIO.Rogue.Logging", 9),
        ("SkathIO.Rogue.Validation.FluentValidation", 3),
    };

    public static TheoryData<string> PackableProjectDirectories()
    {
        var data = new TheoryData<string>();
        foreach (string project in RepoFiles.PackableProjects())
        {
            data.Add(Path.GetDirectoryName(project)!.Replace('\\', '/'));
        }
        return data;
    }

    public static TheoryData<string, int, string> PromotedPackages()
    {
        var data = new TheoryData<string, int, string>();
        foreach ((string project, int promoted, string entry) in _promotedPackages)
        {
            data.Add(project, promoted, entry);
        }
        return data;
    }

    public static TheoryData<string, int> AlreadyShippedPackages()
    {
        var data = new TheoryData<string, int>();
        foreach ((string project, int shipped) in _alreadyShippedPackages)
        {
            data.Add(project, shipped);
        }
        return data;
    }

    // ── Every packable project ────────────────────────────────────────────────────────

    [Fact]
    public void EveryPackageTheRequestNames_IsPackable()
    {
        string[] packable = RepoFiles.PackableProjects();

        foreach (string project in _promotedPackages.Select(static p => p.Project)
            .Concat(_alreadyShippedPackages.Select(static p => p.Project)))
        {
            Assert.Contains($"src/{project}/{project}.csproj", packable);
        }
    }

    [Theory]
    [MemberData(nameof(PackableProjectDirectories))]
    public void PackableProject_HasBothPublicApiFilesAtItsRoot(string directory)
    {
        Assert.True(File.Exists(Path.Combine(RepoFiles.Root, directory, ShippedFile)), $"{directory}/{ShippedFile} is missing");
        Assert.True(File.Exists(Path.Combine(RepoFiles.Root, directory, UnshippedFile)), $"{directory}/{UnshippedFile} is missing");
    }

    [Theory]
    [MemberData(nameof(PackableProjectDirectories))]
    public void Unshipped_HoldsNoEntryBeyondItsHeader(string directory)
    {
        string[] entries = Entries(Path.Combine(directory, UnshippedFile));

        Assert.True(entries.Length == 0,
            $"{directory}/{UnshippedFile} still holds {entries.Length} entries, first: {entries.FirstOrDefault()}");
    }

    [Theory]
    [MemberData(nameof(PackableProjectDirectories))]
    public void Unshipped_KeepsItsHeader(string directory)
    {
        string[] lines = NonBlankLines(Path.Combine(directory, UnshippedFile));

        Assert.Equal(new[] { Header }, lines);
    }

    [Theory]
    [MemberData(nameof(PackableProjectDirectories))]
    public void Shipped_CarriesTheHeaderOnceAsItsFirstLine(string directory)
    {
        string[] lines = NonBlankLines(Path.Combine(directory, ShippedFile));

        Assert.NotEmpty(lines);
        Assert.Equal(Header, lines[0]);
        Assert.Single(lines, static l => l == Header);
    }

    // ── The packages whose API this release ships ─────────────────────────────────────

    [Theory]
    [MemberData(nameof(PromotedPackages))]
    public void Shipped_HoldsEveryPromotedEntry(string project, int promoted, string entry)
    {
        string[] entries = Entries(Path.Combine("src", project, ShippedFile));

        Assert.True(entries.Length >= promoted,
            $"src/{project}/{ShippedFile} holds {entries.Length} entries; the request promotes {promoted}");
        Assert.Contains(entry, entries);
    }

    [Theory]
    [MemberData(nameof(AlreadyShippedPackages))]
    public void Shipped_KeepsTheEntriesItAlreadyHad(string project, int shipped)
    {
        string[] entries = Entries(Path.Combine("src", project, ShippedFile));

        Assert.True(entries.Length >= shipped,
            $"src/{project}/{ShippedFile} holds {entries.Length} entries; it held {shipped} before the release");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    private static string[] NonBlankLines(string relative) =>
        File.ReadLines(Path.Combine(RepoFiles.Root, relative))
            .Select(static l => l.Trim())
            .Where(static l => l.Length > 0)
            .ToArray();

    // An entry is any line that is neither blank nor a '#' directive such as the header.
    private static string[] Entries(string relative) =>
        NonBlankLines(relative).Where(static l => !l.StartsWith('#')).ToArray();
}
