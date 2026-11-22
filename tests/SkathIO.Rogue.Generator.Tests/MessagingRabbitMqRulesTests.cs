using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// Holds the SkathIO.Rogue.Messaging.RabbitMQ package to the rules that are fixed before it is built: it is
/// a transport for the shared messaging core, targets net8.0 and net10.0 only, keeps the project's package,
/// license, public API and no-reflection conventions, never maps foreign JSON by a CLR type name, and ships
/// with Testcontainers RabbitMQ tests that never reach the package itself. The broker behaviours (topology,
/// confirms, prefetch, ack after success, recovery, redelivery then dead-letter) are proved by those container
/// tests, because how the transport is configured is the builder's choice. The package is read from its
/// source and its build output, so this file needs no compile-time reference to it.
/// </summary>
public sealed class MessagingRabbitMqRulesTests
{
    private const string PackageName = "SkathIO.Rogue.Messaging.RabbitMQ";
    private const string ProjectPath = "src/" + PackageName + "/" + PackageName + ".csproj";
    private const string TransportContract = "SkathIO.Rogue.Messaging.IMessageTransport";

    private static readonly Regex _bannedApis = new Regex(
        @"System\.Reflection|GetCustomAttribute|Activator\.CreateInstance|MakeGeneric(Type|Method)|Assembly\.GetTypes|GetAssemblies\(|"
        + @"System\.Linq\.Expressions|Expression<|Type\.GetType\(|AssemblyQualifiedName|BinaryFormatter|NetDataContractSerializer");

    // ── The package and its place in the solution ────────────────────────────────────

    [Fact]
    public void Package_TargetsNet8AndNet10Only_IsPackable_AndIsInTheSolution()
    {
        string project = RepoFiles.Read(ProjectPath);

        Assert.Matches(@"<TargetFrameworks>\s*(net8\.0;net10\.0|net10\.0;net8\.0)\s*</TargetFrameworks>", project);
        Assert.Matches(@"<IsPackable>\s*true\s*</IsPackable>", project);
        Assert.Contains(ProjectPath.Replace('/', '\\'), RepoFiles.Read("SkathIO.Rogue.sln"), StringComparison.Ordinal);
    }

    [Fact]
    public void Package_BuildsOnTheSharedMessagingCore()
    {
        Assert.Matches(@"<ProjectReference\s+Include=""[^""]*SkathIO\.Rogue\.Messaging\.csproj""", RepoFiles.Read(ProjectPath));
    }

    [Fact]
    public void Package_ShipsATransportThatImplementsTheMessagingContract_AndTracksIt()
    {
        string[] transports = BuiltPackage().PublicTypes
            .Where(static t => !t.IsAbstract && t.AllInterfaces.Any(static i => i.ToDisplayString() == TransportContract))
            .Select(static t => t.ToDisplayString()).ToArray();
        // Released entries move to PublicAPI.Shipped.txt, so a type counts as tracked in either file.
        string tracked = File.ReadAllText(Path.Combine(PackageRoot, "PublicAPI.Unshipped.txt"))
            + File.ReadAllText(Path.Combine(PackageRoot, "PublicAPI.Shipped.txt"));

        Assert.NotEmpty(transports);
        Assert.All(transports, t => Assert.Contains(t, tracked, StringComparison.Ordinal));
    }

    // ── Package conventions ──────────────────────────────────────────────────────────

    [Fact]
    public void PackageSource_UsesNoReflectionScanningTypeNameLookupOrBannedSerializer()
    {
        string[] files = SourceFiles();

        Assert.NotEmpty(files);
        Assert.All(files, static f => Assert.DoesNotMatch(_bannedApis, File.ReadAllText(f)));
    }

    [Fact]
    public void Package_ReferencesNoMediatRAndPinsNoVersionInTheProject()
    {
        string project = RepoFiles.Read(ProjectPath);

        Assert.DoesNotMatch(@"Include=""(MediatR|Mediator\.)", project);
        Assert.DoesNotMatch(@"<PackageReference\b[^>]*\bVersion=", project);
        Assert.Matches(@"<PackageReference\s+Include=""RabbitMQ\.Client""", project);
        Assert.Matches(@"<PackageVersion\s+Include=""RabbitMQ\.Client""\s+Version=""[^""]+""", RepoFiles.Read("Directory.Packages.props"));
    }

    [Fact]
    public void PublicApi_IsTrackedInOneFlatPairOfFilesAtTheProjectRoot()
    {
        Assert.True(File.Exists(Path.Combine(PackageRoot, "PublicAPI.Shipped.txt")), "PublicAPI.Shipped.txt is missing at the project root.");
        Assert.True(File.Exists(Path.Combine(PackageRoot, "PublicAPI.Unshipped.txt")), "PublicAPI.Unshipped.txt is missing at the project root.");
        Assert.False(Directory.Exists(Path.Combine(PackageRoot, "PublicAPI")), "The package splits its public API per target framework.");
    }

    [Fact]
    public void EveryPublicMember_HasADocComment()
    {
        string[] files = SourceFiles();
        Assert.NotEmpty(files);
        string[] undocumented = files
            .SelectMany(static f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f).GetRoot().DescendantNodes()
                .OfType<MemberDeclarationSyntax>().Where(IsPublicSurface).Where(static m => !HasDocComment(m))
                .Select(m => Path.GetFileName(f) + ":" + (m.GetLocation().GetLineSpan().StartLinePosition.Line + 1)))
            .ToArray();

        Assert.True(undocumented.Length == 0, "Public members without a /// comment: " + string.Join(", ", undocumented));
    }

    // ── Dependencies and licenses ────────────────────────────────────────────────────

    [Fact]
    public void RestoreGraph_HoldsOnlyLicenseCheckedPackages_EachAnnotatedWithItsLicense()
    {
        string[] allowedLines = RepoFiles.Read(".github/allowed-packages.txt").Split('\n').Select(static l => l.Trim()).ToArray();
        HashSet<string> allowed = allowedLines.Where(static l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] graph = RestoreGraph();

        string[] unlisted = graph.Where(id => !allowed.Contains(id)).ToArray();
        string[] unannotated = graph.Where(id => !allowedLines.Any(l => Regex.IsMatch(l, "^# " + Regex.Escape(id) + @" — \S.* — verified \d{4}-\d{2}-\d{2}", RegexOptions.IgnoreCase))).ToArray();

        Assert.Contains("RabbitMQ.Client", graph, StringComparer.OrdinalIgnoreCase);
        Assert.True(unlisted.Length == 0, "Not license-checked: " + string.Join(", ", unlisted));
        Assert.True(unannotated.Length == 0, "No license annotation: " + string.Join(", ", unannotated));
    }

    [Theory]
    [InlineData("Testcontainers")]
    [InlineData("Hangfire")]
    [InlineData("Newtonsoft.Json")]
    public void RestoreGraph_HoldsNoTestOrBenchOnlyPackage(string family)
    {
        string[] found = RestoreGraph().Where(id => id.Equals(family, StringComparison.OrdinalIgnoreCase)
            || id.StartsWith(family + ".", StringComparison.OrdinalIgnoreCase)).ToArray();

        Assert.True(found.Length == 0, "The package's restore graph holds " + string.Join(", ", found));
    }

    // ── The Testcontainers RabbitMQ tests ────────────────────────────────────────────

    [Fact]
    public void ContainerTests_LiveInATestProjectOfTheSolution_ThatUsesTestcontainersRabbitMq()
    {
        string solution = RepoFiles.Read("SkathIO.Rogue.sln");
        string[] projects = Directory.GetFiles(Path.Combine(RepoFiles.Root, "tests"), "*.csproj", SearchOption.AllDirectories)
            .Where(static p => Regex.IsMatch(File.ReadAllText(p), @"Include=""Testcontainers\.RabbitMq""", RegexOptions.IgnoreCase))
            .Where(static p => Regex.IsMatch(File.ReadAllText(p), @"Include=""[^""]*" + Regex.Escape(PackageName) + @"\.csproj""", RegexOptions.IgnoreCase))
            .Select(static p => Path.GetRelativePath(RepoFiles.Root, p).Replace('/', '\\')).ToArray();

        Assert.Contains(projects, p => solution.Contains(p, StringComparison.Ordinal));
        Assert.Matches(@"<PackageVersion\s+Include=""Testcontainers\.RabbitMq""\s+Version=""[^""]+""", RepoFiles.Read("Directory.Packages.props"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private static string PackageRoot => Path.Combine(RepoFiles.Root, "src", PackageName);

    private static string[] SourceFiles() =>
        Directory.Exists(PackageRoot)
            ? Directory.GetFiles(PackageRoot, "*.cs", SearchOption.AllDirectories)
                .Where(static f => !Regex.IsMatch(f.Replace('\\', '/'), "/(bin|obj)/")).ToArray()
            : Array.Empty<string>();

    // Package ids in the package's restore graph, every target framework.
    private static string[] RestoreGraph()
    {
        string assets = Path.Combine(PackageRoot, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), assets + " is missing; restore the solution first.");
        using JsonDocument graph = JsonDocument.Parse(File.ReadAllText(assets));
        return graph.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(static l => l.Value.GetProperty("type").GetString() == "package")
            .Select(static l => l.Name.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // A member is public surface when it is public (or protected) and every type around it is public.
    private static bool IsPublicSurface(MemberDeclarationSyntax member)
    {
        bool inInterface = member.Parent is InterfaceDeclarationSyntax;
        bool inEnum = member is EnumMemberDeclarationSyntax;
        bool visible = member.Modifiers.Any(static m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword))
            && !member.Modifiers.Any(static m => m.IsKind(SyntaxKind.PrivateKeyword));
        if (!inInterface && !inEnum && !visible)
        {
            return false;
        }
        return member.Ancestors().OfType<BaseTypeDeclarationSyntax>().All(static t => t.Modifiers.Any(static m => m.IsKind(SyntaxKind.PublicKeyword)));
    }

    private static bool HasDocComment(MemberDeclarationSyntax member) =>
        member.GetLeadingTrivia().Any(static t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

    private static BuiltAssembly BuiltPackage() => BuiltAssembly.Load(PackageName);
}

/// <summary>A package's build output read as metadata, so its dependencies need not load.</summary>
internal sealed class BuiltAssembly
{
    private BuiltAssembly(INamedTypeSymbol[] publicTypes)
    {
        PublicTypes = publicTypes;
    }

    public INamedTypeSymbol[] PublicTypes { get; }

    public static BuiltAssembly Load(string package)
    {
        DirectoryInfo output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\'));
        string path = Path.Combine(RepoFiles.Root, "src", package, "bin", output.Parent!.Name, output.Name, package + ".dll");
        Assert.True(File.Exists(path), "The package is not built: " + path + " is missing. Build the solution first.");

        IEnumerable<string> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.dll"))
            .Where(static p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .GroupBy(static p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).Select(static g => g.Last());
        CSharpCompilation compilation = CSharpCompilation.Create("Inspect", references: references.Select(static p => MetadataReference.CreateFromFile(p)));
        IAssemblySymbol assembly = compilation.SourceModule.ReferencedAssemblySymbols.Single(a => a.Name == package);

        return new BuiltAssembly(PublicTypesOf(assembly.GlobalNamespace).ToArray());
    }

    private static IEnumerable<INamedTypeSymbol> PublicTypesOf(INamespaceSymbol ns) =>
        ns.GetTypeMembers().Where(static t => t.DeclaredAccessibility == Accessibility.Public)
            .Concat(ns.GetNamespaceMembers().SelectMany(PublicTypesOf));
}
