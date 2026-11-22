using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The 2.0.0 user guides. The docs folder gains guides for SkathIO.Rogue.Jobs, the messaging core and
/// the outbox and inbox, covering the topics the release asks for. The README and the RabbitMQ guide
/// link them, docs/api-reference.md and docs/getting-started.md list every package new since 1.2.1,
/// the C# samples in all of them name only API that exists, and the guides are committed. The file
/// names of the guides are the builder's choice, so a guide is found by the package it describes.
/// </summary>
public sealed class FeatureGuidesRulesTests
{
    private const string Readme = "README.md";
    private const string RabbitMqGuide = "docs/messaging-rabbitmq.md";
    private const string ApiReference = "docs/api-reference.md";
    private const string GettingStarted = "docs/getting-started.md";
    private const string JobsPackage = "SkathIO.Rogue.Jobs";
    private const string MessagingPackage = "SkathIO.Rogue.Messaging";
    private const string OutboxPackage = "SkathIO.Rogue.Messaging.Outbox";

    // The docs that were there before this milestone; any other markdown file under docs/ is a new guide.
    private static readonly string[] _earlierDocs =
    {
        "api-reference.md", "behaviors.md", "benchmarks.md", "getting-started.md", "governance.md",
        "messaging-rabbitmq.md", "migration-guide.md", "roadmap.md", "validation.md",
    };

    // The packages 1.2.1, the last release, shipped; every other packable project is new in 2.0.0.
    private static readonly string[] _releasedPackages =
    {
        "SkathIO.Rogue", "SkathIO.Rogue.Abstractions", "SkathIO.Rogue.Logging", "SkathIO.Rogue.MediatR",
        "SkathIO.Rogue.Validation.FluentValidation",
    };

    private static readonly Lazy<HashSet<string>> _publicApiTokens = new Lazy<HashSet<string>>(PublicApiTokens);
    private static readonly Lazy<HashSet<string>> _settableMembers = new Lazy<HashSet<string>>(SettableMembers);
    private static readonly Lazy<string> _sourceText = new Lazy<string>(SourceText);

    public static TheoryData<string, string> JobsTopics() => new TheoryData<string, string>
    {
        { "the scheduler", @"\bIJobScheduler\b" },
        { "the [Job] attribute", @"\[Job[\](,]" },
        { "the worker opt-in", @"\bAddRogueJobWorker\b" },
        { "retries", @"(?i)\bretr(y|ies)\b" },
        { "backoff", @"(?i)\bbackoff\b" },
        { "dead-lettering", @"(?i)\bdead[- ]?letter" },
        { "delayed jobs", @"(?i)(\.Schedule\s*\(|\bdelay(ed)?\b)" },
        { "recurring jobs", @"(?i)\brecurring\b" },
        { "unique jobs", @"(?i)\bunique" },
        { "the in-memory store", @"(?i)(InMemoryJobStore|in-memory)" },
        { "the Postgres store", @"(?i)\bPostgres" },
        { "the SQL Server store", @"(?i)(SQL Server|SqlServer)" },
        { "JobsOptions.ScopePerBatch", @"\bScopePerBatch\b" },
        { "the serializer context", @"\bSerializerContext\b" },
        { "ROGUE019", @"\bROGUE019\b" },
    };

    public static TheoryData<string, string> MessagingTopics() => new TheoryData<string, string>
    {
        { "the [Message] attribute", @"\[Message\(" },
        { "AddRogueMessaging", @"\bAddRogueMessaging\b" },
        { "transports", @"(?i)\btransports?\b" },
        { "the in-memory transport", @"(?i)(InMemoryTransport|in-memory transport)" },
        { "the RabbitMQ transport", @"(?i)\bRabbitMQ\b" },
        { "the consumer", @"\bAddRogueMessageConsumer\b" },
        { "own-origin messages", @"(?i)\borigin\b" },
        { "retries", @"(?i)\bretr(y|ies)\b" },
    };

    public static TheoryData<string, string> OutboxTopics() => new TheoryData<string, string>
    {
        { "IMessageOutbox", @"\bIMessageOutbox\b" },
        { "Publish", @"\bPublish\b" },
        { "a DbTransaction", @"\bDbTransaction\b" },
        { "the relay", @"(?i)\brelay" },
        { "the inbox", @"(?i)\binbox" },
    };

    public static TheoryData<string> NewPackages() => ToTheoryData(NewPackageIds());

    public static TheoryData<string> SampleDocs() =>
        ToTheoryData(NewGuides().Concat(new[] { ApiReference, GettingStarted }));

    // ── The guides ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(JobsPackage)]
    [InlineData(MessagingPackage)]
    [InlineData(OutboxPackage)]
    public void ThereIsAGuide_ForEachFamily(string package)
    {
        Assert.True(GuidesFor(package).Length > 0, "No new guide under docs/ names " + package + ".");
    }

    [Theory]
    [MemberData(nameof(JobsTopics))]
    public void TheJobsGuide_CoversEveryTopic(string topic, string pattern)
    {
        AssertCovered(JobsPackage, topic, pattern);
    }

    [Theory]
    [MemberData(nameof(MessagingTopics))]
    public void TheMessagingGuide_CoversEveryTopic(string topic, string pattern)
    {
        AssertCovered(MessagingPackage, topic, pattern);
    }

    [Theory]
    [MemberData(nameof(OutboxTopics))]
    public void TheOutboxGuide_CoversEveryTopic(string topic, string pattern)
    {
        AssertCovered(OutboxPackage, topic, pattern);
    }

    // ── Links ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(JobsPackage)]
    [InlineData(MessagingPackage)]
    [InlineData(OutboxPackage)]
    public void TheReadme_LinksTheGuideOfEachFamily(string package)
    {
        AssertLinksAGuide(Readme, package);
    }

    [Theory]
    [InlineData(MessagingPackage)]
    [InlineData(OutboxPackage)]
    public void TheRabbitMqGuide_LinksTheMessagingAndOutboxGuides(string package)
    {
        AssertLinksAGuide(RabbitMqGuide, package);
    }

    [Fact]
    public void EveryNewGuide_IsLinkedFromTheReadme()
    {
        string[] linked = LinkTargets(Readme);
        string[] guides = NewGuides();

        Assert.NotEmpty(guides);
        Assert.All(guides, g => Assert.Contains(g, linked));
    }

    // ── The package lists ─────────────────────────────────────────────────────────────

    [Fact]
    public void ThereArePackagesNewSinceTheLastRelease()
    {
        Assert.Contains(JobsPackage, NewPackageIds());
        Assert.Contains(MessagingPackage, NewPackageIds());
        Assert.Contains(OutboxPackage, NewPackageIds());
    }

    [Theory]
    [MemberData(nameof(NewPackages))]
    public void TheApiReferencePackagesTable_HasARowForEachNewPackage(string package)
    {
        Assert.Matches(new Regex(@"^\|\s*`" + Regex.Escape(package) + @"`\s*\|", RegexOptions.Multiline), RepoFiles.Read(ApiReference));
    }

    [Theory]
    [MemberData(nameof(NewPackages))]
    public void GettingStarted_NamesEachNewPackage(string package)
    {
        Assert.Matches(PackagePattern(package), RepoFiles.Read(GettingStarted));
    }

    // ── Code samples ──────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(SampleDocs))]
    public void EveryRogueNamespaceASampleImports_Exists(string doc)
    {
        string[] missing = CSharpSamples(doc)
            .SelectMany(static s => Regex.Matches(s, @"^\s*using\s+(?<ns>SkathIO\.Rogue[\w.]*)\s*;", RegexOptions.Multiline))
            .Select(static m => m.Groups["ns"].Value)
            .Where(static ns => !Regex.IsMatch(_sourceText.Value, @"\bnamespace\s+" + Regex.Escape(ns) + @"\s*[;{\r\n]"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, doc + " imports namespaces no package declares: " + string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(SampleDocs))]
    public void EveryAddRogueCallInASample_Exists(string doc)
    {
        string[] missing = CSharpSamples(doc)
            .SelectMany(static s => Regex.Matches(s, @"\b(?<name>AddRogue\w*)\s*[<(]"))
            .Select(static m => m.Groups["name"].Value)
            .Where(static name => !Regex.IsMatch(_sourceText.Value, @"\b" + name + @"\b"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, doc + " calls registrations no package has: " + string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(SampleDocs))]
    public void EveryJobsAndMessagingTypeASampleNames_IsPublicApi(string doc)
    {
        string[] missing = CSharpSamples(doc)
            .SelectMany(static s => Regex.Matches(s, @"\b(?<name>I(?:Job|Message)\w+|\w*(?:Job|Message|Messaging|Outbox|Rogue)\w*Options)\b"))
            .Select(static m => m.Groups["name"].Value)
            .Where(static name => !_publicApiTokens.Value.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, doc + " names types that are not public API: " + string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(SampleDocs))]
    public void EveryOptionASampleSetsInARegistration_IsAPublicSettableMember(string doc)
    {
        string[] missing = CSharpSamples(doc)
            .SelectMany(OptionsSetInRegistrations)
            .Where(static name => !_settableMembers.Value.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, doc + " sets options no options type has: " + string.Join(", ", missing));
    }

    // ── Committed ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryNewGuide_IsCommitted()
    {
        HashSet<string> committed = new HashSet<string>(
            Git("ls-tree", "-r", "--name-only", "HEAD", "--", "docs").Split('\n', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);
        string[] guides = NewGuides();

        Assert.NotEmpty(guides);
        Assert.All(guides, g => Assert.Contains(g, committed));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────

    private static void AssertCovered(string package, string topic, string pattern)
    {
        string[] guides = GuidesFor(package);

        Assert.NotEmpty(guides);
        Assert.True(
            guides.Any(g => Regex.IsMatch(RepoFiles.Read(g), pattern)),
            "No guide for " + package + " (" + string.Join(", ", guides) + ") covers " + topic + ".");
    }

    private static void AssertLinksAGuide(string doc, string package)
    {
        string[] guides = GuidesFor(package);
        string[] linked = LinkTargets(doc);

        Assert.NotEmpty(guides);
        Assert.True(guides.Any(linked.Contains), doc + " links no guide for " + package + " (" + string.Join(", ", guides) + ").");
    }

    // New markdown files under docs/, as "docs/name.md".
    private static string[] NewGuides() =>
        Directory.GetFiles(Path.Combine(RepoFiles.Root, "docs"), "*.md", SearchOption.TopDirectoryOnly)
            .Select(static f => Path.GetFileName(f))
            .Where(static f => !_earlierDocs.Contains(f, StringComparer.Ordinal))
            .Select(static f => "docs/" + f)
            .OrderBy(static f => f, StringComparer.Ordinal)
            .ToArray();

    private static string[] GuidesFor(string package) =>
        NewGuides().Where(g => PackagePattern(package).IsMatch(RepoFiles.Read(g))).ToArray();

    // The package id itself, not a longer id that starts with it (SkathIO.Rogue.Messaging is not
    // SkathIO.Rogue.Messaging.Outbox); a full stop that ends a sentence still counts.
    private static Regex PackagePattern(string package) => new Regex(Regex.Escape(package) + @"(?!\w|\.\w)");

    // Markdown link targets in a file, resolved against its folder, as repository-relative paths.
    private static string[] LinkTargets(string doc)
    {
        string folder = Path.GetDirectoryName(doc)!.Replace('\\', '/');
        return Regex.Matches(RepoFiles.Read(doc), @"\]\((?<target>[^)#\s]+)(#[^)\s]*)?\)")
            .Select(static m => m.Groups["target"].Value)
            .Where(static t => !t.Contains("://", StringComparison.Ordinal))
            .Select(t => Normalize(folder.Length == 0 ? t : folder + "/" + t))
            .ToArray();
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (string part in path.Split('/'))
        {
            if (part == "..")
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else if (part.Length > 0 && part != ".")
            {
                parts.Add(part);
            }
        }
        return string.Join("/", parts);
    }

    private static string[] CSharpSamples(string doc) =>
        Regex.Matches(RepoFiles.Read(doc), @"^\s*```(?:csharp|cs|c#)[^\n]*\n(?<code>.*?)^\s*```", RegexOptions.Multiline | RegexOptions.Singleline)
            .Select(static m => m.Groups["code"].Value)
            .ToArray();

    // The members a sample sets on the options lambda of a registration call, as in
    // AddRogueJobs(o => { o.ScopePerBatch = true; }).
    private static IEnumerable<string> OptionsSetInRegistrations(string sample) =>
        Regex.Matches(sample, @"\bAdd\w+\s*\([^()]*?\b(?<p>\w+)\s*=>")
            .Select(static m => m.Groups["p"].Value)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(p => Regex.Matches(sample, @"(?<![\w.])" + Regex.Escape(p) + @"\.(?<name>\w+)\s*=(?![=>])"))
            .Select(static m => m.Groups["name"].Value);

    private static HashSet<string> PublicApiTokens() =>
        new HashSet<string>(
            PublicApiFiles().SelectMany(static f => Regex.Matches(File.ReadAllText(f), @"\w+").Select(static m => m.Value)),
            StringComparer.Ordinal);

    private static HashSet<string> SettableMembers() =>
        new HashSet<string>(
            PublicApiFiles().SelectMany(static f => Regex.Matches(File.ReadAllText(f), @"\.(?<name>\w+)\.(?:set|init)\s*->").Select(static m => m.Groups["name"].Value)),
            StringComparer.Ordinal);

    private static string[] PublicApiFiles() =>
        Directory.GetFiles(Path.Combine(RepoFiles.Root, "src"), "PublicAPI.*.txt", SearchOption.AllDirectories);

    // Every C# source under src/, so names the generator emits (such as AddRogue) count too.
    private static string SourceText() =>
        string.Join(
            "\n",
            Directory.GetFiles(Path.Combine(RepoFiles.Root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(static f => !Regex.IsMatch(f.Replace('\\', '/'), "/(bin|obj)/"))
                .Select(File.ReadAllText));

    // The package ids of the packable projects that the last release did not have.
    private static string[] NewPackageIds() =>
        RepoFiles.PackableProjects()
            .Select(static p => PackageId(p, Path.GetFileNameWithoutExtension(p)))
            .Where(static id => !_releasedPackages.Contains(id, StringComparer.Ordinal))
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();

    private static string PackageId(string project, string fallback)
    {
        Match id = Regex.Match(RepoFiles.Read(project), @"<PackageId>\s*(?<id>[^<\s]+)\s*</PackageId>");
        return id.Success ? id.Groups["id"].Value : fallback;
    }

    private static TheoryData<string> ToTheoryData(IEnumerable<string> values)
    {
        var data = new TheoryData<string>();
        foreach (string value in values)
        {
            data.Add(value);
        }
        return data;
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
