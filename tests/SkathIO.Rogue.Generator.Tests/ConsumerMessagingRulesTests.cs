using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The messaging scenarios of the end-to-end consumer suite: through the packed packages, two
/// services exchange messages over RabbitMQ with the outbox and the inbox, drop the events they
/// published themselves, keep a failing message's signed delivery count under a shared
/// DeliveryStampKey, defer busy deliveries through the wait queue, and put messages back on shutdown.
/// The tests are deterministic, and those that need a container skip on a machine without Docker but
/// run, and fail without it, in CI.
/// </summary>
public sealed class ConsumerMessagingRulesTests
{
    private const string ConsumerDirectory = "tests/consumer";
    private const string ConsumerSolution = ConsumerDirectory + "/SkathIO.Rogue.Consumer.sln";
    private const string RabbitMqSetup = @"\bAddRogueRabbitMqTransport\s*\(|\bnew\s+RabbitMqTransport\s*\(";
    private const string StampKeyAssignment = @"\bDeliveryStampKey\s*=(?!=)\s*(?<value>[^;,\r\n}]+)";
    private const string ServiceNameAssignment = @"\bServiceName\s*=(?!=)\s*(?<value>[^;,\r\n}]+)";

    private static readonly string[] _skippedDirectories = { "bin", "obj", "TestResults" };

    public static TheoryData<string> MessagingPackages() =>
        new TheoryData<string>("SkathIO.Rogue.Messaging.RabbitMQ", "SkathIO.Rogue.Messaging.Outbox");

    public static TheoryData<string> ScenarioNames() => new TheoryData<string>(Scenarios.Keys);

    // The public API each scenario goes through; every pattern of a scenario must appear.
    private static IReadOnlyDictionary<string, string[]> Scenarios { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["the outbox"] = new[] { @"\bAddRogueMessagingOutbox\s*\(", @"\bIMessageOutbox\b", @"\bBeginTransaction(Async)?\s*\(|\bDbTransaction\b" },
        ["the inbox"] = new[] { @"\bAddRogueMessagingOutbox\s*\(", @"(?i)inbox", @"(?i)duplicat|twice|redeliver|same\s*id" },
        ["the drop of an own-origin event"] = new[] { @"\bIPublisher\b|\.Publish\s*(<[^<>()]*>)?\s*\(", @"(?i)origin" },
        ["signed delivery counts"] = new[] { StampKeyAssignment, @"\bMaxAttempts\b|\bDeliveryCount\b" },
        ["wait-queue deferrals"] = new[] { @"(?i)\bwait\w*queue|\bWaitQueue\b|\bDefer\w*" },
        ["shutdown"] = new[] { @"\.RunAsync\s*\(|\bAddRogueMessageConsumer\s*\(|\.StartAsync\s*\(", @"\.StopAsync\s*\(|\.Cancel(Async)?\s*\(|(?i)shut\s*down" },
    };

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void ConsumerTests_RunEachMessagingScenario_OverRabbitMq(string scenario)
    {
        string covered = MessagingText();
        string[] missing = Scenarios[scenario].Where(p => !Regex.IsMatch(covered, p)).ToArray();

        Assert.True(missing.Length == 0, "The consumer tests over RabbitMQ do not cover " + scenario + "; nothing matches: " + string.Join(" ; ", missing));
    }

    [Fact]
    public void ConsumerTests_RunTwoServices_UnderDistinctNames()
    {
        string[] names = ServiceNames(MessagingText());

        Assert.True(names.Length >= 2, "Expected two services over RabbitMQ with different service names, found: " + string.Join(", ", names));
    }

    [Fact]
    public void ConsumerTests_GiveTheirServices_ASharedDeliveryStampKey()
    {
        string[] keys = Regex.Matches(MessagingText(), StampKeyAssignment).Select(static m => m.Groups["value"].Value.Trim()).ToArray();
        bool shared = keys.Distinct(StringComparer.Ordinal).Count() == 1 || keys.GroupBy(static k => k, StringComparer.Ordinal).Any(static g => g.Count() >= 2);

        Assert.NotEmpty(keys);
        Assert.True(shared, "Each service instance sets a DeliveryStampKey of its own, so no key is shared: " + string.Join(", ", keys));
    }

    [Theory]
    [MemberData(nameof(MessagingPackages))]
    public void ConsumerProjects_RestoreTheMessagingPackages_ByPackageReference(string package)
    {
        string[] files = MessagingUnits().Select(static u => u.File).Distinct(StringComparer.Ordinal).ToArray();
        string[] projects = files.Select(ProjectOf).Distinct(StringComparer.Ordinal).ToArray();
        string[] missing = projects.Where(p => !PackageReferences(p).Contains(package, StringComparer.OrdinalIgnoreCase)).ToArray();

        Assert.NotEmpty(files);
        Assert.DoesNotContain(string.Empty, projects);
        Assert.True(missing.Length == 0, "These consumer projects run messaging over RabbitMQ without a package reference to " + package + ": " + string.Join(", ", missing));
    }

    [Fact]
    public void RabbitMqTests_SkipWithoutDocker_ButNotInCi()
    {
        string covered = MessagingText();

        Assert.Matches(@"GetEnvironmentVariable\s*\(\s*""(CI|GITHUB_ACTIONS)""\s*\)", covered);
        Assert.Matches(@"\bSkip\b", covered);
        Assert.Matches(@"(?i)docker", covered);
    }

    [Fact]
    public void MessagingConsumerTests_DoNotSleep_NorAssertOnTheWallClock()
    {
        string[] messagingFiles = ConsumerSourceFiles().Where(static f => Regex.IsMatch(File.ReadAllText(f), @"(?m)^\s*using\s+SkathIO\.Rogue\.Messaging(\.\w+)*\s*;")).ToArray();
        string[] clocked = messagingFiles
            .Where(static f => Regex.IsMatch(File.ReadAllText(f), @"\bThread\.Sleep\s*\(|\bAssert\.\w+\s*\([^;]*\b(Elapsed\w*|Stopwatch|DateTime(Offset)?\.(Utc)?Now)\b"))
            .Select(static f => Path.GetRelativePath(RepoFiles.Root, f))
            .ToArray();

        Assert.NotEmpty(messagingFiles);
        Assert.True(clocked.Length == 0, "These messaging tests sleep or assert on the wall clock, so they are not deterministic: " + string.Join(", ", clocked));
    }

    // The service names given to the transports: literals, constants, and literal arguments of the
    // methods or constructors whose parameter becomes the service name.
    private static string[] ServiceNames(string text) =>
        Regex.Matches(text, ServiceNameAssignment)
            .SelectMany(m => NamesFor(text, m.Groups["value"].Value.Trim()))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    // The names one assignment's value gives: itself as a literal or a constant, or else, as a
    // parameter, each literal or constant passed for it.
    private static IEnumerable<string> NamesFor(string text, string value)
    {
        if ((Literal(value) ?? ConstantValue(text, value)) is { } direct)
        {
            return new[] { direct };
        }
        return ArgumentsFor(text, value).Select(a => Literal(a) ?? ConstantValue(text, a)).OfType<string>();
    }

    private static string? Literal(string value) =>
        Regex.Match(value.Trim(), @"^""(?<text>[^""\\]*)""$") is { Success: true } m ? m.Groups["text"].Value : null;

    private static string? ConstantValue(string text, string name) =>
        !Regex.IsMatch(name, @"^[A-Za-z_]\w*$") ? null : Regex.Match(text, @"\bstring\??\s+" + Regex.Escape(name.Trim()) + @"\b(\s*\{[^}]*\})?\s*=\s*""(?<text>[^""\\]*)""") is { Success: true } m ? m.Groups["text"].Value : null;

    // The arguments of every call to a method or constructor that takes a string parameter of this name.
    private static IEnumerable<string> ArgumentsFor(string text, string parameter)
    {
        if (!Regex.IsMatch(parameter, @"^[A-Za-z_]\w*$"))
        {
            return Array.Empty<string>();
        }
        string[] callees = Regex.Matches(text, @"\b(?<callee>[A-Za-z_]\w*)\s*(<[^<>()]*>)?\s*\([^()]*\bstring\??\s+" + Regex.Escape(parameter) + @"\b")
            .Select(static m => m.Groups["callee"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return callees
            .SelectMany(c => Regex.Matches(text, @"\b" + Regex.Escape(c) + @"\s*(<[^<>()]*>)?\s*\((?<args>[^()]*)\)").Select(static m => m.Groups["args"].Value))
            .SelectMany(static args => args.Split(','))
            .Select(static a => Regex.Replace(a.Trim(), @"^\w+\s*:\s*", string.Empty));
    }

    // The consumer solution's projects, as full paths.
    private static string[] ConsumerProjects()
    {
        string directory = Path.Combine(RepoFiles.Root, ConsumerDirectory);
        return Regex.Matches(RepoFiles.Read(ConsumerSolution), @"""(?<path>[^""]+\.csproj)""")
            .Select(m => Path.GetFullPath(Path.Combine(directory, m.Groups["path"].Value.Replace('\\', '/'))))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> SourcesUnder(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            yield return file;
        }
        foreach (string sub in Directory.EnumerateDirectories(directory).Where(static d => !_skippedDirectories.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)))
        {
            foreach (string file in SourcesUnder(sub))
            {
                yield return file;
            }
        }
    }

    private static string[] ConsumerSourceFiles() =>
        ConsumerProjects().Select(static p => Path.GetDirectoryName(p)!).Where(Directory.Exists).SelectMany(SourcesUnder).Distinct(StringComparer.Ordinal).ToArray();

    // The consumer project a source file belongs to, or empty when it is in none.
    private static string ProjectOf(string file) =>
        ConsumerProjects().Where(p => file.StartsWith(Path.GetDirectoryName(p)! + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderByDescending(static p => p.Length)
            .FirstOrDefault() ?? string.Empty;

    // The package ids a consumer project restores directly, through the consumer projects it references,
    // or through the build files beside the consumer solution.
    private static string[] PackageReferences(string project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(new[] { project });
        var text = new List<string>(Directory.GetFiles(Path.Combine(RepoFiles.Root, ConsumerDirectory), "Directory.Build.*").Select(File.ReadAllText));
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            if (!seen.Add(current) || !File.Exists(current))
            {
                continue;
            }
            string csproj = File.ReadAllText(current);
            text.Add(csproj);
            foreach (Match reference in Regex.Matches(csproj, @"<ProjectReference\s+Include=""(?<path>[^""]+)"""))
            {
                pending.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, reference.Groups["path"].Value.Replace('\\', '/'))));
            }
        }
        return text.SelectMany(static t => Regex.Matches(t, @"<PackageReference\s+Include=""(?<id>[^""]+)""").Select(static m => m.Groups["id"].Value)).ToArray();
    }

    private const string TypeStart =
        @"(?m)^(?<!\][ \t]*\r?\n)(?=(?:[ \t]*\[[^\n]*\][ \t]*\r?\n)*[ \t]*(?:\[[^\n]*\][ \t]*)*(?:(?:public|internal|private|protected|sealed|abstract|static|partial|file|readonly)[ \t]+)*(?:class|record|struct|interface)[ \t]+[A-Za-z_])";

    // Each type declaration in the consumer sources, with its attributes, up to the next declaration.
    private static SourceUnit[] Units() =>
        ConsumerSourceFiles()
            .SelectMany(static f => Regex.Split(File.ReadAllText(f), TypeStart).Select(t => new SourceUnit(f, DeclaredName(t), t)))
            .ToArray();

    private static string? DeclaredName(string text) =>
        Regex.Match(text, @"\b(class|record|struct|interface)\s+(?<name>[A-Za-z_]\w*)") is { Success: true } m ? m.Groups["name"].Value : null;

    // The type declarations that set up the RabbitMQ transport.
    private static SourceUnit[] MessagingUnits() => Units().Where(static u => Regex.IsMatch(u.Text, RabbitMqSetup)).ToArray();

    // The text of the RabbitMQ declarations and of the types that use them (the test classes), two
    // levels up, with the declarations of the types all of these use, two levels down.
    private static string MessagingText()
    {
        SourceUnit[] units = Units();
        ILookup<string, SourceUnit> declarations = units.Where(static u => u.Name is not null).ToLookup(static u => u.Name!, StringComparer.Ordinal);
        var included = new HashSet<SourceUnit>(MessagingUnits());
        Assert.True(included.Count > 0, "No consumer test sets up the RabbitMQ transport.");
        for (int depth = 0; depth < 2; depth++)
        {
            string[] names = included.Where(static u => u.Name is not null).Select(static u => u.Name!).ToArray();
            foreach (SourceUnit user in units.Where(u => names.Any(n => Regex.IsMatch(u.Text, @"\b" + Regex.Escape(n) + @"\b"))).ToArray())
            {
                included.Add(user);
            }
        }
        for (int depth = 0; depth < 2; depth++)
        {
            string[] used = included.SelectMany(static u => Regex.Matches(u.Text, @"\b[A-Za-z_]\w*\b").Select(static m => m.Value)).Distinct(StringComparer.Ordinal).ToArray();
            foreach (SourceUnit declared in used.SelectMany(n => declarations[n].Concat(declarations[n + "Attribute"])).ToArray())
            {
                included.Add(declared);
            }
        }
        return string.Join("\n", included.Select(static u => u.Text));
    }

    private sealed record SourceUnit(string File, string? Name, string Text);
}
