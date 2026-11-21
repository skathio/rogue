using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace SkathIO.Rogue.Consumer.Tests;

/// <summary>
/// What each packed package holds: its libraries, the generator and analyzers in the analyzers/
/// folder (where the compiler loads them from a package), and its dependencies, which decide what a
/// consumer gets transitively.
/// </summary>
public sealed class PackagingTests
{
    private const string Rogue = "SkathIO.Rogue";
    private const string Abstractions = "SkathIO.Rogue.Abstractions";
    private const string Jobs = "SkathIO.Rogue.Jobs";
    private const string Messaging = "SkathIO.Rogue.Messaging";
    private const string DependencyInjection = "Microsoft.Extensions.DependencyInjection.Abstractions";
    private const string Hosting = "Microsoft.Extensions.Hosting.Abstractions";
    private const string Logging = "Microsoft.Extensions.Logging.Abstractions";

    private static readonly string[] _targetFrameworks = { "net8.0", "net10.0" };

    // Each package's direct dependencies, the same for both target frameworks.
    private static readonly Dictionary<string, string[]> _dependencies = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Abstractions] = Array.Empty<string>(),
        [Rogue] = new[] { Abstractions, DependencyInjection },
        ["SkathIO.Rogue.Logging"] = new[] { Rogue, Logging },
        ["SkathIO.Rogue.MediatR"] = new[] { Rogue, DependencyInjection },
        ["SkathIO.Rogue.Validation"] = new[] { Rogue },
        ["SkathIO.Rogue.Validation.FluentValidation"] = new[] { Rogue, "FluentValidation" },
        [Jobs] = new[] { Rogue, DependencyInjection, Hosting, Logging },
        ["SkathIO.Rogue.Jobs.Postgres"] = new[] { Jobs, "Npgsql" },
        ["SkathIO.Rogue.Jobs.SqlServer"] = new[] { Jobs, "Microsoft.Data.SqlClient" },
        [Messaging] = new[] { Rogue, DependencyInjection, Hosting, Logging },
        ["SkathIO.Rogue.Messaging.RabbitMQ"] = new[] { Messaging, "RabbitMQ.Client" },
        ["SkathIO.Rogue.Messaging.Outbox"] = new[] { Jobs, Messaging },
    };

    // The Roslyn components each package carries in analyzers/dotnet/cs.
    private static readonly Dictionary<string, string[]> _analyzers = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Rogue] = new[] { "SkathIO.Rogue.SourceGenerator.dll" },
        ["SkathIO.Rogue.Validation.FluentValidation"] = new[] { "SkathIO.Rogue.Validation.FluentValidation.SourceGenerator.dll" },
        ["SkathIO.Rogue.MediatR"] = new[] { "SkathIO.Rogue.Migration.Analyzer.dll" },
        [Jobs] = new[] { "SkathIO.Rogue.Jobs.Analyzers.dll", "SkathIO.Rogue.Jobs.CodeFixes.dll" },
    };

    public static TheoryData<string> Packages() => new TheoryData<string>(_dependencies.Keys);

    [Fact]
    public void TheFeed_HoldsTheTwelvePackages_AtTheRestoredVersion()
    {
        string version = ConsumerPaths.RestoredVersion();
        string[] files = Directory.GetFiles(ConsumerPaths.Feed, "*.nupkg").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(_dependencies.Keys.Select(id => id + "." + version + ".nupkg").Order(StringComparer.Ordinal), files);
    }

    [Theory]
    [MemberData(nameof(Packages))]
    public void EachPackage_ShipsItsLibrary_ForNet8AndNet10Only(string id)
    {
        string[] libraries = Entries(id).Where(static e => e.StartsWith("lib/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(_targetFrameworks.Select(tfm => "lib/" + tfm + "/" + id + ".dll").Order(StringComparer.Ordinal), libraries);
    }

    [Theory]
    [MemberData(nameof(Packages))]
    public void EachPackage_ShipsItsGeneratorsAndAnalyzers_InTheAnalyzersFolder_AndNowhereElse(string id)
    {
        string[] expected = _analyzers.TryGetValue(id, out string[]? dlls) ? dlls : Array.Empty<string>();
        string[] entries = Entries(id);
        string[] inAnalyzers = entries.Where(static e => e.StartsWith("analyzers/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected.Select(static d => "analyzers/dotnet/cs/" + d).Order(StringComparer.Ordinal), inAnalyzers);
        Assert.DoesNotContain(entries, e => !e.StartsWith("analyzers/", StringComparison.Ordinal) && _analyzers.Values.SelectMany(static d => d).Any(d => e.EndsWith("/" + d, StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(Packages))]
    public void EachPackage_DependsOnExactlyItsDirectDependencies_WithRoguePackagesAtTheSameVersion(string id)
    {
        string version = ConsumerPaths.RestoredVersion();
        XElement[] groups = DependencyGroups(id);

        Assert.Equal(_targetFrameworks.Order(StringComparer.Ordinal), groups.Select(static g => (string?)g.Attribute("targetFramework") ?? "").Order(StringComparer.Ordinal));
        foreach (XElement group in groups)
        {
            XElement[] dependencies = group.Elements().Where(static e => e.Name.LocalName == "dependency").ToArray();
            Assert.Equal(_dependencies[id].Order(StringComparer.Ordinal), dependencies.Select(static d => (string)d.Attribute("id")!).Order(StringComparer.Ordinal));
            Assert.All(
                dependencies.Where(static d => ((string)d.Attribute("id")!).StartsWith(Rogue, StringComparison.Ordinal)),
                d => Assert.Equal(version, (string?)d.Attribute("version")));
        }
    }

    private static string PackagePath(string id) => Path.Combine(ConsumerPaths.Feed, id + "." + ConsumerPaths.RestoredVersion() + ".nupkg");

    private static string[] Entries(string id)
    {
        using ZipArchive zip = ZipFile.OpenRead(PackagePath(id));
        return zip.Entries.Select(static e => e.FullName).ToArray();
    }

    private static XElement[] DependencyGroups(string id)
    {
        using ZipArchive zip = ZipFile.OpenRead(PackagePath(id));
        ZipArchiveEntry nuspec = Assert.Single(zip.Entries, static e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal) && !e.FullName.Contains('/', StringComparison.Ordinal));
        using Stream stream = nuspec.Open();
        XDocument document = XDocument.Load(stream);
        return document.Descendants().Where(static e => e.Name.LocalName == "group" && e.Parent?.Name.LocalName == "dependencies").ToArray();
    }
}
