using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// Holds the SkathIO.Rogue.Jobs core package to its rules. A job is a Rogue command, optionally
/// marked <c>[Job]</c>; the core generator's jobs module maps each job kind to a typed invoker with no
/// reflection, expression trees or type-name lookup, and leaves every other request's generated
/// <c>Send_X</c> exactly as it was. Payloads go through System.Text.Json source generation, and an
/// analyzer with a code fix reports a job type the <c>JsonSerializerContext</c> does not cover. The
/// package targets net8.0 and net10.0 only, tracks its public API, brings no package that has not
/// passed the human license check, and has its own telemetry. Consumer code is compiled in memory
/// against the built package, so this file needs no compile-time reference to it.
/// </summary>
public sealed class JobsCoreTests
{
    private const string PackageName = "SkathIO.Rogue.Jobs";
    private const string DispatcherHint = "RogueDispatcher.g.cs";
    private const string JobMarker = "[global::SkathIO.Rogue.Jobs.Job] ";
    private const string HelpLinkBase = "https://skathio.github.io/rogue/diagnostics/";

    // Two job commands (void and with a result) where the marker goes, next to unmarked requests of
    // every kind. "{MARK}" is replaced by the [Job] marker, or by nothing for the run without the package.
    private const string RequestsTemplate = @"
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;

namespace App
{
    {MARK}public sealed record SendInvoice(int Id) : ICommand;
    public sealed class SendInvoiceHandler : ICommandHandler<SendInvoice>
    {
        public ValueTask Handle(SendInvoice command, CancellationToken cancellationToken) => default;
    }

    {MARK}public sealed record ChargeCard(int Amount) : ICommand<int>;
    public sealed class ChargeCardHandler : ICommandHandler<ChargeCard, int>
    {
        public ValueTask<int> Handle(ChargeCard command, CancellationToken cancellationToken) => new ValueTask<int>(command.Amount);
    }

    public sealed record PlainCommand(int Id) : ICommand;
    public sealed class PlainCommandHandler : ICommandHandler<PlainCommand>
    {
        public ValueTask Handle(PlainCommand command, CancellationToken cancellationToken) => default;
    }

    public sealed record PlainResultCommand(int Id) : ICommand<int>;
    public sealed class PlainResultCommandHandler : ICommandHandler<PlainResultCommand, int>
    {
        public ValueTask<int> Handle(PlainResultCommand command, CancellationToken cancellationToken) => new ValueTask<int>(command.Id);
    }

    public sealed record PlainQuery(int Id) : IQuery<int>;
    public sealed class PlainQueryHandler : IQueryHandler<PlainQuery, int>
    {
        public ValueTask<int> Handle(PlainQuery query, CancellationToken cancellationToken) => new ValueTask<int>(query.Id);
    }
}
";

    // Two [Job] commands and the consumer's JsonSerializerContext; "{COVERED}" holds the
    // [JsonSerializable] lines under test. The context's members are written out by hand because the
    // System.Text.Json generator does not run in these in-memory compilations.
    private const string SerializationTemplate = @"
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;
using SkathIO.Rogue.Jobs;

namespace App
{
    [Job]
    public sealed record SendInvoice(int Id) : ICommand;
    public sealed class SendInvoiceHandler : ICommandHandler<SendInvoice>
    {
        public ValueTask Handle(SendInvoice command, CancellationToken cancellationToken) => default;
    }

    [Job]
    public sealed record ChargeCard(int Amount) : ICommand<int>;
    public sealed class ChargeCardHandler : ICommandHandler<ChargeCard, int>
    {
        public ValueTask<int> Handle(ChargeCard command, CancellationToken cancellationToken) => new ValueTask<int>(command.Amount);
    }

{COVERED}
    public partial class JobsJson : JsonSerializerContext
    {
        public JobsJson() : base(null) { }
        protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
        public override JsonTypeInfo? GetTypeInfo(System.Type type) => null;
    }
}
";

    private const string CoverSendInvoice = "    [JsonSerializable(typeof(SendInvoice))]";
    private const string CoverChargeCard = "    [JsonSerializable(typeof(ChargeCard))]";

    private static readonly string[] _reflectionApis =
    {
        "System.Reflection", "GetCustomAttribute", "Activator.CreateInstance", "MakeGenericType",
        "MakeGenericMethod", "GetProperties(", "GetProperty(", "GetMethod(", "Assembly.GetTypes",
        "AppDomain.CurrentDomain.GetAssemblies", "MethodInfo", "System.Linq.Expressions", "Expression<",
        "Type.GetType(", "AssemblyQualifiedName", "BinaryFormatter", "NetDataContractSerializer",
    };

    private static readonly Lazy<ImmutableArray<DiagnosticAnalyzer>> _analyzers =
        new Lazy<ImmutableArray<DiagnosticAnalyzer>>(() => Instantiate<DiagnosticAnalyzer>().ToImmutableArray());

    private static readonly Lazy<CodeFixProvider[]> _codeFixes =
        new Lazy<CodeFixProvider[]>(() => Instantiate<CodeFixProvider>().ToArray());

    // ── The jobs module leaves every other request as it was ─────────────────────────

    [Theory]
    [InlineData("Send_App_PlainCommand")]
    [InlineData("Send_App_PlainResultCommand")]
    [InlineData("Send_App_PlainQuery")]
    public void UnmarkedRequest_WithThePackageAndNoJob_IsGeneratedAsBefore(string sendMethod)
    {
        string before = SendMembers(Dispatcher(RunGenerator(Requests(marked: false), withPackage: false)), sendMethod);
        string after = SendMembers(Dispatcher(RunGenerator(Requests(marked: false), withPackage: true)), sendMethod);

        Assert.NotEmpty(before);
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData("Send_App_PlainCommand")]
    [InlineData("Send_App_PlainResultCommand")]
    [InlineData("Send_App_PlainQuery")]
    public void UnmarkedRequest_NextToJobCommands_IsGeneratedAsBefore(string sendMethod)
    {
        string before = SendMembers(Dispatcher(RunGenerator(Requests(marked: false), withPackage: false)), sendMethod);
        string after = SendMembers(Dispatcher(RunGenerator(Requests(marked: true), withPackage: true)), sendMethod);

        Assert.NotEmpty(before);
        Assert.Equal(before, after);
    }

    // ── Generated kind-to-invoker map ────────────────────────────────────────────────

    [Theory]
    [InlineData("SendInvoice")]
    [InlineData("ChargeCard")]
    [InlineData("PlainCommand")]
    [InlineData("PlainResultCommand")]
    public void EveryCommand_GetsGeneratedJobCode_WhenThePackageIsReferenced(string command)
    {
        int before = Mentions(RunGenerator(Requests(marked: false), withPackage: false), command);
        int after = Mentions(RunGenerator(Requests(marked: true), withPackage: true), command);

        Assert.True(after > before, "The jobs module generated nothing for App." + command + ": it is named " + before + " times without the package and " + after + " times with it.");
    }

    [Fact]
    public void GeneratedJobCode_UsesNoReflectionExpressionTreesOrTypeNameLookup()
    {
        GeneratorDriverRunResult result = RunGenerator(Requests(marked: true), withPackage: true);

        foreach (GeneratedSourceResult source in result.Results.SelectMany(static r => r.GeneratedSources))
        {
            AssertNoBannedApi(source.HintName, source.SourceText.ToString());
        }
    }

    [Fact]
    public void ConsumerWithJobCommands_CompilesWithNoErrorsAndNoGeneratedWarnings()
    {
        string source = Requests(marked: true);
        GeneratorDriverRunResult result = RunGenerator(source, withPackage: true);
        CSharpCompilation compilation = Compile(GeneratedTrees(result).Prepend(Parse(source)), References(withPackage: true));

        Diagnostic[] errors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Diagnostic[] generatedWarnings = compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Warning && IsGenerated(d.Location.SourceTree))
            .Concat(result.Diagnostics.Where(static d => d.Severity >= DiagnosticSeverity.Warning))
            .ToArray();

        Assert.True(errors.Length == 0, "The consumer failed to compile:\n" + string.Join("\n", errors.Select(static d => d.ToString())));
        Assert.True(generatedWarnings.Length == 0, "Generated code warns:\n" + string.Join("\n", generatedWarnings.Select(static d => d.ToString())));
    }

    [Fact]
    public void JobsPackage_BuiltWithTheCore_RaisesNoRogue013()
    {
        GeneratorDriverRunResult result = RunGenerator(Requests(marked: true), withPackage: true);

        Assert.DoesNotContain(result.Diagnostics, static d => d.Id == "ROGUE013");
    }

    // ── Serializer coverage analyzer and code fix ────────────────────────────────────

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UncoveredJobType_IsReported(bool sendInvoiceCovered, bool chargeCardCovered)
    {
        Diagnostic[] reported = await AnalyzeAsync(Serialization(sendInvoiceCovered, chargeCardCovered));

        Assert.NotEmpty(reported);
    }

    [Fact]
    public async Task EveryJobTypeCovered_NothingIsReported()
    {
        Diagnostic[] reported = await AnalyzeAsync(Serialization(sendInvoiceCovered: true, chargeCardCovered: true));

        Assert.True(reported.Length == 0, "A fully covered context is reported:\n" + string.Join("\n", reported.Select(static d => d.ToString())));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CodeFix_AddsTheMissingJsonSerializable_AndClearsTheReport(bool sendInvoiceCovered, bool chargeCardCovered)
    {
        (string fixedSource, Diagnostic[] remaining) = await FixAllAsync(Serialization(sendInvoiceCovered, chargeCardCovered));

        Assert.True(remaining.Length == 0, "Still reported after the code fix:\n" + string.Join("\n", remaining.Select(static d => d.ToString())));
        Assert.Matches(CoveredPattern("SendInvoice"), fixedSource);
        Assert.Matches(CoveredPattern("ChargeCard"), fixedSource);
    }

    [Fact]
    public async Task CoverageDiagnostic_FollowsTheProjectDiagnosticConventions()
    {
        Diagnostic reported = (await AnalyzeAsync(Serialization(sendInvoiceCovered: false, chargeCardCovered: false))).First();
        DiagnosticDescriptor descriptor = reported.Descriptor;

        Assert.Matches(@"^ROGUE\d{3}$", descriptor.Id);
        Assert.NotEqual("ROGUE007", descriptor.Id);
        Assert.Equal("SkathIO.Rogue", descriptor.Category);
        Assert.Equal(HelpLinkBase + descriptor.Id, descriptor.HelpLinkUri);
        Assert.True(descriptor.IsEnabledByDefault);
        Assert.Single(DeclaredDiagnosticIds(), id => id == descriptor.Id);
        Assert.Matches(new Regex("^" + descriptor.Id + @"\s*\|\s*SkathIO\.Rogue\s*\|\s*(Error|Warning|Info)\s*\|\s*\S", RegexOptions.Multiline), AnalyzerReleaseRows());
    }

    // ── Package conventions ──────────────────────────────────────────────────────────

    [Fact]
    public void Package_TargetsNet8AndNet10Only()
    {
        string project = File.ReadAllText(PackageFile(PackageName + ".csproj"));
        Match tfms = Regex.Match(project, @"<TargetFrameworks?>([^<]*)</TargetFrameworks?>");

        Assert.True(tfms.Success, "The package project declares no target framework.");
        string[] targets = tfms.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(new[] { "net10.0", "net8.0" }, targets.OrderBy(static t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void Solution_IncludesThePackage()
    {
        string solution = File.ReadAllText(Path.Combine(RepoRoot(), "SkathIO.Rogue.sln"));

        Assert.Contains(@"src\SkathIO.Rogue.Jobs\SkathIO.Rogue.Jobs.csproj", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageSource_UsesNoReflectionExpressionTreesOrTypeNameLookup()
    {
        string[] files = PackageSourceFiles();

        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            AssertNoBannedApi(file, File.ReadAllText(file));
        }
    }

    [Fact]
    public void PackageSource_SerializesThroughSourceGeneratedJson()
    {
        string source = string.Concat(PackageSourceFiles().Select(File.ReadAllText));

        Assert.Matches(@"\bJsonTypeInfo\b|\bJsonSerializerContext\b", source);
    }

    [Fact]
    public void PackageSource_HasItsOwnActivitySourceAndMeter()
    {
        string source = string.Concat(PackageSourceFiles().Select(File.ReadAllText));

        Assert.Matches(@"new\s+(global::)?(System\.Diagnostics\.)?ActivitySource\s*\(", source);
        Assert.Matches(@"new\s+(global::)?(System\.Diagnostics\.Metrics\.)?Meter\s*\(|\bIMeterFactory\b", source);
    }

    [Theory]
    [InlineData("SkathIO.Rogue.Jobs.JobAttribute.JobAttribute() -> void")]
    [InlineData("IJobScheduler.Enqueue")]
    [InlineData("IJobScheduler.EnqueueMany")]
    public void PublicApi_TracksTheNamedMember(string member)
    {
        Assert.True(Directory.Exists(PackageDirectory()), PackageDirectory() + " is missing.");
        string[] apiFiles = Directory.GetFiles(PackageDirectory(), "PublicAPI.*.txt", SearchOption.AllDirectories)
            .Where(static f => !IsBuildOutput(f))
            .ToArray();

        Assert.NotEmpty(apiFiles);
        Assert.Contains(apiFiles, f => File.ReadAllText(f).Contains(member, StringComparison.Ordinal));
    }

    [Fact]
    public void Package_ReferencesOnlyLicenseCheckedPackages()
    {
        string project = File.ReadAllText(PackageFile(PackageName + ".csproj"));
        HashSet<string> allowed = File.ReadAllLines(Path.Combine(RepoRoot(), ".github", "allowed-packages.txt"))
            .Select(static l => l.Trim())
            .Where(static l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] referenced = Regex.Matches(project, @"<PackageReference\s+Include=""([^""]+)""").Select(static m => m.Groups[1].Value).ToArray();

        Assert.All(referenced, id => Assert.True(allowed.Contains(id), id + " is not in .github/allowed-packages.txt; it needs the maintainer's license check first."));
    }

    [Fact]
    public void PackageProject_ReferencesNoMediatR()
    {
        string project = File.ReadAllText(PackageFile(PackageName + ".csproj"));

        Assert.DoesNotMatch(@"Include=""(MediatR|Mediator\.)", project);
    }

    [Fact]
    public void EveryRoslynProject_StaysOnNetstandard20()
    {
        string[] roslynProjects = Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(static p => Regex.IsMatch(File.ReadAllText(p), @"<PackageReference\s+Include=""Microsoft\.CodeAnalysis\.CSharp(\.Workspaces)?"""))
            .ToArray();

        Assert.NotEmpty(roslynProjects);
        Assert.All(roslynProjects, static p => Assert.Matches(@"<TargetFramework>\s*netstandard2\.0\s*</TargetFramework>", File.ReadAllText(p)));
    }

    [Fact]
    public void Changelog_Release200_DescribesThePackage()
    {
        string changelog = File.ReadAllText(Path.Combine(RepoRoot(), "CHANGELOG.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        Match release = Regex.Match(changelog, @"^## \[2\.0\.0\][^\n]*\n(.*?)(?=^## \[)", RegexOptions.Multiline | RegexOptions.Singleline);

        Assert.True(release.Success, "CHANGELOG.md has no [2.0.0] section.");
        Assert.Contains("IJobScheduler", release.Groups[1].Value, StringComparison.Ordinal);
    }

    // ── Helpers: sources and generator runs ──────────────────────────────────────────

    private static string Requests(bool marked) => RequestsTemplate.Replace("{MARK}", marked ? JobMarker : string.Empty, StringComparison.Ordinal);

    private static string Serialization(bool sendInvoiceCovered, bool chargeCardCovered)
    {
        var covered = new List<string>();
        if (sendInvoiceCovered)
        {
            covered.Add(CoverSendInvoice);
        }
        if (chargeCardCovered)
        {
            covered.Add(CoverChargeCard);
        }
        return SerializationTemplate.Replace("{COVERED}", string.Join("\n", covered), StringComparison.Ordinal);
    }

    private static Regex CoveredPattern(string type) =>
        new Regex(@"JsonSerializable(Attribute)?\s*\(\s*typeof\s*\(\s*(global::)?(App\.)?" + type + @"\s*\)");

    private static GeneratorDriverRunResult RunGenerator(string source, bool withPackage)
    {
        CSharpCompilation compilation = Compile(new[] { Parse(source) }, References(withPackage));
        GeneratorDriverRunResult result = GeneratorTestHelper.CreateDriver().RunGenerators(compilation).GetRunResult();
        Assert.Empty(result.Results.Where(static r => r.Exception is not null));
        return result;
    }

    private static IEnumerable<SyntaxTree> GeneratedTrees(GeneratorDriverRunResult result) =>
        result.Results.SelectMany(static r => r.GeneratedSources).Select(static s => Parse(s.SourceText.ToString(), "Generated/" + s.HintName));

    private static bool IsGenerated(SyntaxTree? tree) => tree is not null && tree.FilePath.StartsWith("Generated/", StringComparison.Ordinal);

    private static int Mentions(GeneratorDriverRunResult result, string type)
    {
        var pattern = new Regex(@"\bApp\." + type + @"\b");
        return result.Results.SelectMany(static r => r.GeneratedSources).Sum(s => pattern.Matches(s.SourceText.ToString()).Count);
    }

    private static string Dispatcher(GeneratorDriverRunResult result)
    {
        GeneratedSourceResult dispatcher = Assert.Single(
            result.Results.SelectMany(static r => r.GeneratedSources),
            static s => s.HintName == DispatcherHint);
        return dispatcher.SourceText.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    // The dispatcher members that make up one request's Send_X and its Send_X_* companions, each from
    // its signature line to its closing brace.
    private static string SendMembers(string dispatcher, string methodName)
    {
        IEnumerable<string> kept = _member.Matches(dispatcher)
            .Select(static m => m.Value)
            .Where(m =>
            {
                string signature = m.Substring(0, m.IndexOf('\n', StringComparison.Ordinal));
                return signature.Contains(" " + methodName + "(", StringComparison.Ordinal)
                    || signature.Contains(" " + methodName + "_", StringComparison.Ordinal);
            });
        return string.Join("\n", kept);
    }

    private static readonly Regex _member = new Regex(
        @"^        (internal|private|public|protected)\b.*?\n        \}$",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static void AssertNoBannedApi(string name, string text)
    {
        foreach (string api in _reflectionApis)
        {
            Assert.False(text.Contains(api, StringComparison.Ordinal), name + " uses " + api + ".");
        }
    }

    private static SyntaxTree Parse(string source, string path = "") =>
        CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path);

    private static CSharpCompilationOptions CompilationOptions() =>
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);

    private static CSharpCompilation Compile(IEnumerable<SyntaxTree> trees, IEnumerable<MetadataReference> references) =>
        CSharpCompilation.Create("JobsConsumer", trees, references, CompilationOptions());

    // ── Helpers: analyzer and code fix ───────────────────────────────────────────────

    // Runs the core generator, then every analyzer the repo builds, over the consumer; returns what the
    // analyzers report at Warning or above. An analyzer that throws fails the test instead.
    private static async Task<Diagnostic[]> AnalyzeAsync(string source)
    {
        Project project = ConsumerProject(source, out _);
        return await AnalyzeProjectAsync(project);
    }

    private static async Task<Diagnostic[]> AnalyzeProjectAsync(Project project)
    {
        Compilation compilation = (await project.GetCompilationAsync())!;
        ImmutableArray<Diagnostic> all = await compilation.WithAnalyzers(_analyzers.Value).GetAnalyzerDiagnosticsAsync();

        Assert.DoesNotContain(all, static d => d.Id == "AD0001");
        return all.Where(static d => d.Severity >= DiagnosticSeverity.Warning).ToArray();
    }

    // Applies the offered code fix to each report in turn until nothing is reported, and returns the
    // consumer source after the fixes with whatever is still reported.
    private static async Task<(string Source, Diagnostic[] Remaining)> FixAllAsync(string source)
    {
        Project project = ConsumerProject(source, out DocumentId userDocument);
        Diagnostic[] reported = await AnalyzeProjectAsync(project);
        Assert.NotEmpty(reported);

        for (int round = 0; round < 8 && reported.Length > 0; round++)
        {
            project = await ApplyFirstFixAsync(project, reported[0]);
            reported = await AnalyzeProjectAsync(project);
        }

        string text = (await project.GetDocument(userDocument)!.GetTextAsync()).ToString();
        return (text, reported);
    }

    private static async Task<Project> ApplyFirstFixAsync(Project project, Diagnostic diagnostic)
    {
        Document document = project.GetDocument(diagnostic.Location.SourceTree)
            ?? throw new InvalidOperationException(diagnostic.Id + " is reported outside any document: " + diagnostic);
        var actions = new List<CodeAction>();
        foreach (CodeFixProvider fix in _codeFixes.Value.Where(f => f.FixableDiagnosticIds.Contains(diagnostic.Id)))
        {
            await fix.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None));
        }

        foreach (CodeAction action in actions)
        {
            ImmutableArray<CodeActionOperation> operations = await action.GetOperationsAsync(CancellationToken.None);
            ApplyChangesOperation? apply = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
            if (apply is not null)
            {
                return apply.ChangedSolution.GetProject(project.Id)!;
            }
        }
        throw new InvalidOperationException("No code fix changes the source for " + diagnostic);
    }

    // The consumer as a workspace project: its own source plus the core generator's output as plain
    // documents, so analyzers and code fixes see what a real build would.
    private static Project ConsumerProject(string source, out DocumentId userDocument)
    {
        GeneratorDriverRunResult generated = RunGenerator(source, withPackage: true);
        var workspace = new AdhocWorkspace();
        ProjectId id = ProjectId.CreateNewId();
        Solution solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            id, VersionStamp.Create(), "JobsConsumer", "JobsConsumer", LanguageNames.CSharp,
            compilationOptions: CompilationOptions(),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: References(withPackage: true)));

        userDocument = DocumentId.CreateNewId(id);
        solution = solution.AddDocument(userDocument, "Jobs.cs", SourceText.From(source));
        foreach (GeneratedSourceResult file in generated.Results.SelectMany(static r => r.GeneratedSources))
        {
            solution = solution.AddDocument(DocumentId.CreateNewId(id), file.HintName, file.SourceText);
        }
        return solution.GetProject(id)!;
    }

    // Every DiagnosticAnalyzer or CodeFixProvider in the netstandard2.0 assemblies the repo's src
    // projects build, wherever the builder put them.
    private static IEnumerable<T> Instantiate<T>() where T : class =>
        AnalyzerAssemblies()
            .SelectMany(static a => LoadableTypes(a))
            .Where(static t => typeof(T).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(static t => (T)Activator.CreateInstance(t)!);

    private static Assembly[] AnalyzerAssemblies()
    {
        Dictionary<string, string> host = HostAssemblies();
        string configuration = TestOutputDirectory().Parent!.Name;
        return Directory.GetDirectories(Path.Combine(RepoRoot(), "src"))
            .Select(dir => Path.Combine(dir, "bin", configuration, "netstandard2.0", Path.GetFileName(dir) + ".dll"))
            .Where(File.Exists)
            .Select(path => LoadOnce(path, host))
            .ToArray();
    }

    private static readonly Dictionary<string, Assembly> _loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);

    private static Assembly LoadOnce(string path, Dictionary<string, string> host)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        lock (_loaded)
        {
            if (!_loaded.TryGetValue(name, out Assembly? assembly))
            {
                Assembly? already = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
                assembly = already ?? (host.ContainsKey(name) ? Assembly.Load(new AssemblyName(name)) : AssemblyLoadContext.Default.LoadFromAssemblyPath(path));
                _loaded[name] = assembly;
            }
            return assembly;
        }
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static t => t is not null).Select(static t => t!);
        }
    }

    private static string[] DeclaredDiagnosticIds() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static f => !f.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal) && !f.Replace('\\', '/').Contains("/bin/", StringComparison.Ordinal))
            .SelectMany(static f => Regex.Matches(File.ReadAllText(f), @"id:\s*""(ROGUE\d{3})""").Select(static m => m.Groups[1].Value))
            .ToArray();

    private static string AnalyzerReleaseRows() =>
        string.Concat(Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "AnalyzerReleases.*.md", SearchOption.AllDirectories).Select(File.ReadAllText));

    // ── Helpers: references and repo paths ───────────────────────────────────────────

    private static readonly string[] _baseAssemblies =
    {
        "System.Private.CoreLib", "System.Runtime", "System.Collections", "System.Threading.Tasks", "System.Linq",
        "netstandard", "System.ComponentModel", "System.Diagnostics.DiagnosticSource", "System.Text.Json",
        "System.Text.Encodings.Web", "System.Memory",
        "Microsoft.Extensions.DependencyInjection.Abstractions", "SkathIO.Rogue.Abstractions", "SkathIO.Rogue",
    };

    // The assemblies a consumer of the core package compiles against, plus (when asked) the built
    // jobs package and what it references that the test host has.
    private static MetadataReference[] References(bool withPackage)
    {
        Dictionary<string, string> host = HostAssemblies();
        var names = new HashSet<string>(_baseAssemblies.Where(host.ContainsKey), StringComparer.Ordinal);
        var paths = new List<string>();
        if (withPackage)
        {
            string package = PackageAssemblyPath();
            paths.Add(package);
            names.UnionWith(ReferencedAssemblyNames(package).Where(n => host.ContainsKey(n) && n != PackageName));
        }
        paths.AddRange(names.Select(n => host[n]));
        return paths.Select(static p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
    }

    private static Dictionary<string, string> HostAssemblies() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(static p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .GroupBy(static p => Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);

    private static IEnumerable<string> ReferencedAssemblyNames(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        System.Reflection.Metadata.MetadataReader reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        return reader.AssemblyReferences
            .Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))
            .ToArray();
    }

    private static DirectoryInfo TestOutputDirectory() =>
        new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    // The package assembly built for this test run's configuration and framework, or the copy the
    // host already loads when a project reference brings it in.
    private static string PackageAssemblyPath()
    {
        if (HostAssemblies().TryGetValue(PackageName, out string? hosted))
        {
            return hosted;
        }

        DirectoryInfo testDir = TestOutputDirectory();
        string built = Path.Combine(PackageDirectory(), "bin", testDir.Parent!.Name, testDir.Name, PackageName + ".dll");
        Assert.True(File.Exists(built), "The " + PackageName + " package is not built: " + built + " is missing. Build the solution first.");
        return built;
    }

    private static string PackageDirectory() => Path.Combine(RepoRoot(), "src", PackageName);

    private static string PackageFile(string name)
    {
        string path = Path.Combine(PackageDirectory(), name);
        Assert.True(File.Exists(path), path + " is missing.");
        return path;
    }

    private static string[] PackageSourceFiles()
    {
        Assert.True(Directory.Exists(PackageDirectory()), PackageDirectory() + " is missing.");
        return Directory.GetFiles(PackageDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(static f => !IsBuildOutput(f))
            .ToArray();
    }

    private static bool IsBuildOutput(string path)
    {
        string relative = Path.GetRelativePath(PackageDirectory(), path).Replace('\\', '/');
        return relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SkathIO.Rogue.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
