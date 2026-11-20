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
/// Holds the outbox and inbox to their rules on both database stores. A bus publish takes the caller's own
/// database transaction (Npgsql on Postgres, SqlClient on SQL Server), in a package built on the jobs store
/// and the messaging core, so the message is a row the jobs store keeps and a job relays. That package keeps
/// the project's conventions: net8.0 and net10.0 only, central package pins, no MediatR, no reflection, a
/// tracked and documented public API and a license-checked restore graph. Rolling back the caller's
/// transaction publishing nothing, and a duplicate delivery running its handler once, are proved by container
/// tests on each store that skip without Docker and run in CI, because only a real database shows them. The
/// packages are read from their build output and source, so this file needs no compile-time reference to them.
/// </summary>
public sealed class MessagingOutboxInboxRulesTests
{
    private static readonly Regex _bannedApis = new Regex(
        @"System\.Reflection|GetCustomAttribute|Activator\.CreateInstance|MakeGeneric(Type|Method)|Assembly\.GetTypes|GetAssemblies\(|"
        + @"System\.Linq\.Expressions|Expression<|Type\.GetType\(|AssemblyQualifiedName|BinaryFormatter|NetDataContractSerializer");

    // A test that touches messaging: the bus, a transport, the consumer, an envelope or a [Message] type.
    private static readonly Regex _messaging = new Regex(@"\b(IMessageBus|IMessageTransport|MessageEnvelope|MessageConsumer|InMemoryTransport)\b|\[Message\(");

    private static readonly Lazy<OutboxPublish[]> _publishes = new Lazy<OutboxPublish[]>(FindTransactionalPublishes);

    // ── A bus publish inside the caller's database transaction ───────────────────────

    [Theory]
    [InlineData("Postgres")]
    [InlineData("SqlServer")]
    public void BusPublish_TakesTheCallersDatabaseTransaction(string store)
    {
        Assert.True(PublishesFor(store).Length > 0,
            "No packable package exposes a public Publish method that takes a " + Provider.Of(store).TransactionType + " (or a DbTransaction).");
    }

    [Theory]
    [InlineData("Postgres")]
    [InlineData("SqlServer")]
    public void TransactionalPublish_IsBuiltOnTheJobsStoreAndTheMessagingCore(string store)
    {
        OutboxPublish[] publishes = PublishesFor(store);

        Assert.NotEmpty(publishes);
        Assert.All(publishes, static p => Assert.Contains("SkathIO.Rogue.Jobs", p.References));
        Assert.All(publishes, static p => Assert.Contains("SkathIO.Rogue.Messaging", p.References));
    }

    [Theory]
    [InlineData("Postgres")]
    [InlineData("SqlServer")]
    public void TransactionalPublish_IsTrackedInTheFlatPublicApiFiles_WithADocComment(string store)
    {
        OutboxPublish[] publishes = PublishesFor(store);

        Assert.NotEmpty(publishes);
        foreach (OutboxPublish publish in publishes)
        {
            string root = Path.Combine(RepoFiles.Root, "src", publish.Package);
            Assert.True(File.Exists(Path.Combine(root, "PublicAPI.Shipped.txt")), publish.Package + " has no PublicAPI.Shipped.txt at its root.");
            Assert.False(Directory.Exists(Path.Combine(root, "PublicAPI")), publish.Package + " splits its public API per target framework.");
            string[] unshipped = File.ReadAllLines(Path.Combine(root, "PublicAPI.Unshipped.txt"));
            Assert.Contains(unshipped, l => l.Contains(publish.Type + "." + publish.Method + "(", StringComparison.Ordinal)
                || l.Contains(publish.Type + "." + publish.Method + "<", StringComparison.Ordinal));
            Assert.True(IsDocumented(publish), publish.Type + "." + publish.Method + " has no /// comment.");
        }
    }

    // ── Conventions of the package that holds the outbox ─────────────────────────────

    [Theory]
    [InlineData("Postgres")]
    [InlineData("SqlServer")]
    public void OutboxPackage_IsPackable_TargetsNet8AndNet10Only_PinsNoVersion_AndReferencesNoMediatR(string store)
    {
        foreach (string package in OutboxPackages(store))
        {
            string project = RepoFiles.Read(ProjectPath(package));

            Assert.Matches(@"<IsPackable>\s*true\s*</IsPackable>", project);
            Assert.Matches(@"<TargetFrameworks>\s*(net8\.0;net10\.0|net10\.0;net8\.0)\s*</TargetFrameworks>", project);
            Assert.DoesNotMatch(@"<PackageReference\b[^>]*\bVersion\s*=", project);
            Assert.DoesNotMatch(@"Include=""(MediatR|Mediator\.)", project);
            Assert.Contains(ProjectPath(package).Replace('/', '\\'), RepoFiles.Read("SkathIO.Rogue.sln"), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Postgres")]
    [InlineData("SqlServer")]
    public void OutboxPackageSource_UsesNoReflectionScanningOrBannedSerializer(string store)
    {
        foreach (string package in OutboxPackages(store))
        {
            string[] files = SourceFiles(Path.Combine(RepoFiles.Root, "src", package));

            Assert.NotEmpty(files);
            Assert.All(files, static f => Assert.DoesNotMatch(_bannedApis, File.ReadAllText(f)));
        }
    }

    [Theory]
    [InlineData("Postgres")]
    [InlineData("SqlServer")]
    public void OutboxPackage_RestoreGraph_HoldsOnlyLicenseCheckedPackages_AndNoTestOrBenchOnlyOne(string store)
    {
        string[] allowedLines = RepoFiles.Read(".github/allowed-packages.txt").Split('\n').Select(static l => l.Trim()).ToArray();
        HashSet<string> allowed = allowedLines.Where(static l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string package in OutboxPackages(store))
        {
            string[] graph = RestoreGraph(package);
            string[] unlisted = graph.Where(id => !allowed.Contains(id)).ToArray();
            string[] unannotated = graph.Where(id => !allowedLines.Any(l => Regex.IsMatch(l, "^# " + Regex.Escape(id) + @" — \S.* — verified \d{4}-\d{2}-\d{2}", RegexOptions.IgnoreCase))).ToArray();
            string[] testOnly = graph.Where(static id => Regex.IsMatch(id, @"^(Testcontainers|Hangfire|Newtonsoft\.Json)(\.|$)", RegexOptions.IgnoreCase)).ToArray();

            Assert.True(unlisted.Length == 0, package + " restores packages that are not license-checked: " + string.Join(", ", unlisted));
            Assert.True(unannotated.Length == 0, package + " restores packages with no license annotation: " + string.Join(", ", unannotated));
            Assert.True(testOnly.Length == 0, package + " restores test or bench only packages: " + string.Join(", ", testOnly));
        }
    }

    // ── Container tests on each store ────────────────────────────────────────────────

    [Theory]
    [InlineData("Postgres")]
    [InlineData("SqlServer")]
    public void RollbackOfTheCallersTransaction_IsProvedToPublishNothing_ByAContainerTest(string store)
    {
        string[] found = ContainerTests(store)
            .Where(static m => m.Body.Contains("Rollback", StringComparison.Ordinal) && Regex.IsMatch(m.Body, @"\bPublish\w*\s*(<[^>]*>)?\s*\("))
            .Select(static m => m.Name).ToArray();

        Assert.True(found.Length > 0, "No Docker-gated " + store + " test publishes inside a transaction and rolls it back.");
    }

    [Theory]
    [InlineData("Postgres")]
    [InlineData("SqlServer")]
    public void DuplicateDelivery_IsProvedToRunTheHandlerOnce_ByAContainerTest(string store)
    {
        string[] found = ContainerTests(store)
            .Where(static m => Regex.IsMatch(m.Name, "duplicat|twice|redeliver", RegexOptions.IgnoreCase) && _messaging.IsMatch(m.ClassText + m.Body))
            .Select(static m => m.Name).ToArray();

        Assert.True(found.Length > 0, "No Docker-gated " + store + " test delivers a message twice.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private static OutboxPublish[] PublishesFor(string store) =>
        _publishes.Value.Where(p => Provider.Of(store).Accepts(p.TransactionType)).ToArray();

    private static string[] OutboxPackages(string store)
    {
        string[] packages = PublishesFor(store).Select(static p => p.Package).Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(packages.Length > 0, "No package publishes inside a " + store + " transaction.");
        return packages;
    }

    private static string ProjectPath(string package) => "src/" + package + "/" + package + ".csproj";

    // Every public Publish method of a packable package that takes a database transaction.
    private static OutboxPublish[] FindTransactionalPublishes()
    {
        DirectoryInfo output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\'));
        var found = new List<OutboxPublish>();
        foreach (string project in RepoFiles.PackableProjects())
        {
            string package = Path.GetFileNameWithoutExtension(project);
            string dll = Path.Combine(RepoFiles.Root, "src", package, "bin", output.Parent!.Name, output.Name, package + ".dll");
            if (!File.Exists(dll))
            {
                continue;
            }
            found.AddRange(PublishesIn(package));
        }
        return found.ToArray();
    }

    private static IEnumerable<OutboxPublish> PublishesIn(string package)
    {
        INamedTypeSymbol[] types = BuiltAssembly.Load(package).PublicTypes;
        string[] references = types.Length == 0 ? Array.Empty<string>() : types[0].ContainingAssembly.Modules
            .SelectMany(static m => m.ReferencedAssemblies).Select(static a => a.Name).ToArray();
        return types.SelectMany(static t => t.GetMembers().OfType<IMethodSymbol>().Select(m => (Type: t, Method: m)))
            .Where(static x => x.Method.DeclaredAccessibility == Accessibility.Public && x.Method.Name.StartsWith("Publish", StringComparison.Ordinal))
            .SelectMany(static x => x.Method.Parameters.Select(p => (x.Type, x.Method, Transaction: p.Type.ToDisplayString())))
            .Where(static x => Provider.IsTransaction(x.Transaction))
            .Select(x => new OutboxPublish(package, x.Type.ToDisplayString(), x.Method.Name, x.Transaction, references));
    }

    private static bool IsDocumented(OutboxPublish publish)
    {
        string shortType = publish.TransactionType.Substring(publish.TransactionType.LastIndexOf('.') + 1);
        MethodDeclarationSyntax[] declarations = SourceFiles(Path.Combine(RepoFiles.Root, "src", publish.Package))
            .SelectMany(static f => CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Where(m => m.Identifier.Text == publish.Method && m.ParameterList.Parameters.Any(p => p.Type?.ToString().EndsWith(shortType, StringComparison.Ordinal) == true))
            .Where(m => m.Parent is BaseTypeDeclarationSyntax owner && publish.Type.EndsWith("." + owner.Identifier.Text, StringComparison.Ordinal))
            .ToArray();
        return declarations.Length > 0 && declarations.All(static m => m.GetLeadingTrivia().Any(static t =>
            t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)));
    }

    private static string[] SourceFiles(string root) =>
        Directory.Exists(root)
            ? Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Where(static f => !Regex.IsMatch(f.Replace('\\', '/'), "/(bin|obj)/")).ToArray()
            : Array.Empty<string>();

    private static string[] RestoreGraph(string package)
    {
        string assets = Path.Combine(RepoFiles.Root, "src", package, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), assets + " is missing; restore the solution first.");
        using JsonDocument graph = JsonDocument.Parse(File.ReadAllText(assets));
        return graph.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(static l => l.Value.GetProperty("type").GetString() == "package")
            .Select(static l => l.Name.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Tests marked [StoreFact] in a class that is, or is a base of, a [RequiresDocker] class of a test project
    // in the solution that starts the store's container. A base class shared by both stores counts for each.
    private static TestMethod[] ContainerTests(string store)
    {
        Provider provider = Provider.Of(store);
        TestClass[] all = TestClass.InTestProjects();
        TestClass[] concrete = all.Where(c => c.ContainerPackages.Contains(provider.ContainerPackage)
            && (c.ContainerPackages.Length == 1 || provider.Mentioned(c.Text))).ToArray();
        var chain = new HashSet<TestClass>();
        foreach (TestClass leaf in concrete)
        {
            TestClass[] bases = TestClass.Chain(leaf, all);
            if (bases.Any(static c => c.RequiresDocker))
            {
                chain.UnionWith(bases);
            }
        }
        return chain.SelectMany(static c => c.StoreFacts).ToArray();
    }

    private sealed record OutboxPublish(string Package, string Type, string Method, string TransactionType, string[] References);

    private sealed record TestMethod(string Name, string Body, string ClassText);

    private sealed record Provider(string TransactionType, string ContainerPackage, string Words)
    {
        private static readonly string[] _generic = { "System.Data.Common.DbTransaction", "System.Data.IDbTransaction" };

        public static Provider Of(string store) => store == "Postgres"
            ? new Provider("Npgsql.NpgsqlTransaction", "Testcontainers.PostgreSql", "Postgres|Npgsql")
            : new Provider("Microsoft.Data.SqlClient.SqlTransaction", "Testcontainers.MsSql", "SqlServer|SqlClient|MsSql");

        public static bool IsTransaction(string type) =>
            _generic.Contains(type) || type == Of("Postgres").TransactionType || type == Of("SqlServer").TransactionType;

        public bool Accepts(string type) => type == TransactionType || _generic.Contains(type);

        public bool Mentioned(string text) => Regex.IsMatch(text, Words);
    }

    private sealed class TestClass
    {
        private TestClass(ClassDeclarationSyntax syntax, string[] containerPackages)
        {
            Name = syntax.Identifier.Text;
            Text = syntax.ToString();
            BaseNames = syntax.BaseList?.Types.Select(static t => Regex.Replace(t.Type.ToString(), @"<.*$|^.*\.", string.Empty)).ToArray() ?? Array.Empty<string>();
            RequiresDocker = syntax.AttributeLists.SelectMany(static l => l.Attributes).Any(static a => Regex.IsMatch(a.Name.ToString(), @"(^|\.)RequiresDocker(Attribute)?$"));
            ContainerPackages = containerPackages;
            StoreFacts = syntax.Members.OfType<MethodDeclarationSyntax>()
                .Where(static m => m.AttributeLists.SelectMany(static l => l.Attributes).Any(static a => Regex.IsMatch(a.Name.ToString(), @"(^|\.)StoreFact(Attribute)?$")))
                .Select(m => new TestMethod(m.Identifier.Text, m.ToString(), Text)).ToArray();
        }

        public string Name { get; }

        public string Text { get; }

        public string[] BaseNames { get; }

        public bool RequiresDocker { get; }

        public string[] ContainerPackages { get; }

        public TestMethod[] StoreFacts { get; }

        public static TestClass[] InTestProjects()
        {
            string solution = RepoFiles.Read("SkathIO.Rogue.sln");
            return Directory.GetFiles(Path.Combine(RepoFiles.Root, "tests"), "*.csproj", SearchOption.AllDirectories)
                .Where(p => solution.Contains(Path.GetRelativePath(RepoFiles.Root, p).Replace('/', '\\'), StringComparison.Ordinal))
                .SelectMany(static p => ClassesOf(p)).ToArray();
        }

        public static TestClass[] Chain(TestClass leaf, TestClass[] all)
        {
            var chain = new List<TestClass> { leaf };
            for (int i = 0; i < chain.Count; i++)
            {
                chain.AddRange(all.Where(c => chain[i].BaseNames.Contains(c.Name) && !chain.Contains(c)));
            }
            return chain.ToArray();
        }

        private static IEnumerable<TestClass> ClassesOf(string project)
        {
            string[] containers = Regex.Matches(File.ReadAllText(project), @"Include=""(Testcontainers\.[A-Za-z]+)""")
                .Select(static m => m.Groups[1].Value).ToArray();
            return SourceFiles(Path.GetDirectoryName(project)!)
                .SelectMany(static f => CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                .Select(c => new TestClass(c, containers));
        }
    }
}
