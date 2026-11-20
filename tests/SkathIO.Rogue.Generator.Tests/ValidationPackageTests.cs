using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// Holds the SkathIO.Rogue.Validation package to its rules. A <c>[Validate]</c> partial request is
/// checked by its rule attributes, its static <c>Rules(x, ref ValidationErrors)</c> hook and any
/// <c>IAsyncRules&lt;T&gt;</c> resolved from DI, inside its generated <c>Send_X</c> and before the
/// handler runs. A failure throws <c>RogueValidationException</c>, whose <c>ToProblemDictionary()</c>
/// keys each message by its path, nested and collection paths included (<c>Lines[2].Sku</c>). The
/// valid path allocates nothing, and a request without <c>[Validate]</c> is generated exactly as
/// before. The consumer code is compiled in memory against the built package, so this file needs no
/// compile-time reference to it.
/// </summary>
[Collection(RealDiDispatchCollection.Name)]
public sealed class ValidationPackageTests
{
    private const string PackageName = "SkathIO.Rogue.Validation";
    private const string DispatcherHint = "RogueDispatcher.g.cs";

    private static readonly Lazy<Consumer> _consumer = new Lazy<Consumer>(Consumer.Build);

    // A plain request with no marker: its generated Send_X must not change when the package is there.
    private const string PlainSource = @"
namespace App
{
    public sealed record PlainOrder(string Sku) : global::SkathIO.Rogue.ICommand<int>;
    public sealed class PlainOrderHandler : global::SkathIO.Rogue.ICommandHandler<PlainOrder, int>
    {
        public global::System.Threading.Tasks.ValueTask<int> Handle(PlainOrder command, global::System.Threading.CancellationToken cancellationToken)
            => new global::System.Threading.Tasks.ValueTask<int>(1);
    }
}
";

    // Validated requests of every kind (response command, void command, query), a nested [Validate]
    // type, a collection of [Validate] items, a Rules hook and an IAsyncRules<T> class that needs a
    // service only DI can give it.
    private const string ValidatedSource = @"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkathIO.Rogue;
using SkathIO.Rogue.Generated;
using SkathIO.Rogue.Validation;

namespace App
{
    public static class Calls
    {
        public static int Handled;
    }

    [Validate]
    public sealed partial record OrderLine
    {
        [NotEmpty] public string Sku { get; init; } = ""x"";
    }

    [Validate]
    public sealed partial record Customer
    {
        [NotEmpty] public string Name { get; init; } = ""x"";
    }

    [Validate]
    public sealed partial record CreateOrder : ICommand<int>
    {
        [NotEmpty] public string Reference { get; init; } = ""x"";
        public Customer Customer { get; init; } = new Customer();
        public IReadOnlyList<OrderLine> Lines { get; init; } = Array.Empty<OrderLine>();
        public int From { get; init; }
        public int To { get; init; }

        private static void Rules(CreateOrder x, ref ValidationErrors errors)
        {
            if (x.From > x.To)
            {
                errors.Add(""Window"", ""order.window.reversed"", ""The window starts after it ends."");
                errors.Add(""Window"", ""order.window.too_far"", ""The window start is out of range."");
            }
        }
    }

    public sealed class CreateOrderHandler : ICommandHandler<CreateOrder, int>
    {
        public ValueTask<int> Handle(CreateOrder command, CancellationToken cancellationToken)
        {
            Calls.Handled++;
            return new ValueTask<int>(1);
        }
    }

    [Validate]
    public sealed partial record ArchiveOrder : ICommand
    {
        [NotEmpty] public string Reference { get; init; } = ""x"";
    }

    public sealed class ArchiveOrderHandler : ICommandHandler<ArchiveOrder>
    {
        public ValueTask Handle(ArchiveOrder command, CancellationToken cancellationToken)
        {
            Calls.Handled++;
            return default;
        }
    }

    [Validate]
    public sealed partial record FindOrder : IQuery<int>
    {
        [NotEmpty] public string Reference { get; init; } = ""x"";
    }

    public sealed class FindOrderHandler : IQueryHandler<FindOrder, int>
    {
        public ValueTask<int> Handle(FindOrder query, CancellationToken cancellationToken)
        {
            Calls.Handled++;
            return new ValueTask<int>(2);
        }
    }

    public interface IReferenceRegistry
    {
        bool IsTaken(string reference);
    }

    public sealed class ReferenceRegistry : IReferenceRegistry
    {
        public bool IsTaken(string reference) => reference == ""taken"";
    }

    [Validate]
    public sealed partial record ReserveReference : ICommand<int>
    {
        [NotEmpty] public string Reference { get; init; } = ""x"";
    }

    public sealed class ReserveReferenceHandler : ICommandHandler<ReserveReference, int>
    {
        public ValueTask<int> Handle(ReserveReference command, CancellationToken cancellationToken)
        {
            Calls.Handled++;
            return new ValueTask<int>(3);
        }
    }

    public sealed class ReserveReferenceChecks : IAsyncRules<ReserveReference>
    {
        private readonly IReferenceRegistry _registry;

        public ReserveReferenceChecks(IReferenceRegistry registry)
        {
            _registry = registry;
        }

        public async ValueTask CheckAsync(ReserveReference x, ValidationErrors errors, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (_registry.IsTaken(x.Reference))
            {
                errors.Add(""Reference"", ""reference.taken"", ""The reference is already used."");
            }
        }
    }

    public sealed record PlainOrder(string Sku) : ICommand<int>;
    public sealed class PlainOrderHandler : ICommandHandler<PlainOrder, int>
    {
        public ValueTask<int> Handle(PlainOrder command, CancellationToken cancellationToken)
        {
            Calls.Handled++;
            return new ValueTask<int>(1);
        }
    }

    public static class Probe
    {
        public static void Register(IServiceCollection services)
        {
            services.TryAddSingleton<IReferenceRegistry, ReferenceRegistry>();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IAsyncRules<ReserveReference>, ReserveReferenceChecks>());
        }

        private static OrderLine[] Lines(params string[] skus) => skus.Select(s => new OrderLine { Sku = s }).ToArray();

        private static CreateOrder Order(string reference, string customer, params string[] skus)
            => new CreateOrder { Reference = reference, Customer = new Customer { Name = customer }, Lines = Lines(skus) };

        private static Task Dispatch(ISender sender, RogueDispatcher dispatcher, string scenario) => scenario switch
        {
            ""valid"" => sender.Send(Order(""R-1"", ""Ada"", ""A"", ""B"", ""C"")).AsTask(),
            ""no-lines"" => sender.Send(Order(""R-1"", ""Ada"")).AsTask(),
            ""reference-empty"" => sender.Send(Order("""", ""Ada"", ""A"")).AsTask(),
            ""customer-name-empty"" => sender.Send(Order(""R-1"", """", ""A"")).AsTask(),
            ""only-line-empty"" => sender.Send(Order(""R-1"", ""Ada"", """")).AsTask(),
            ""third-line-empty"" => sender.Send(Order(""R-1"", ""Ada"", ""A"", ""B"", """")).AsTask(),
            ""first-and-third-line-empty"" => sender.Send(Order(""R-1"", ""Ada"", """", ""B"", """")).AsTask(),
            ""reference-and-line-empty"" => sender.Send(Order("""", ""Ada"", ""A"", """")).AsTask(),
            ""window-reversed"" => sender.Send(Order(""R-1"", ""Ada"", ""A"") with { From = 5, To = 1 }).AsTask(),
            ""window-ordered"" => sender.Send(Order(""R-1"", ""Ada"", ""A"") with { From = 1, To = 5 }).AsTask(),
            ""concrete-reference-empty"" => dispatcher.SendCreateOrder(Order("""", ""Ada"", ""A"")).AsTask(),
            ""concrete-valid"" => dispatcher.SendCreateOrder(Order(""R-1"", ""Ada"", ""A"")).AsTask(),
            ""reserve-free"" => sender.Send(new ReserveReference { Reference = ""free"" }).AsTask(),
            ""reserve-taken"" => sender.Send(new ReserveReference { Reference = ""taken"" }).AsTask(),
            ""archive-valid"" => sender.Send(new ArchiveOrder { Reference = ""R-1"" }).AsTask(),
            ""archive-empty"" => sender.Send(new ArchiveOrder { Reference = """" }).AsTask(),
            ""find-valid"" => sender.Send(new FindOrder { Reference = ""R-1"" }).AsTask(),
            ""find-empty"" => sender.Send(new FindOrder { Reference = """" }).AsTask(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };

        public static async Task<(int Handled, string? Thrown, KeyValuePair<string, string[]>[]? Problems)> Send(IServiceProvider root, string scenario)
        {
            using IServiceScope scope = root.CreateScope();
            ISender sender = scope.ServiceProvider.GetRequiredService<ISender>();
            RogueDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<RogueDispatcher>();
            int before = Calls.Handled;
            try
            {
                await Dispatch(sender, dispatcher, scenario);
                return (Calls.Handled - before, null, null);
            }
            catch (RogueValidationException ex)
            {
                IEnumerable<KeyValuePair<string, string[]>> problems = ex.ToProblemDictionary();
                return (Calls.Handled - before, nameof(RogueValidationException), problems.ToArray());
            }
            catch (Exception ex)
            {
                return (Calls.Handled - before, ex.GetType().FullName + "": "" + ex.Message, null);
            }
        }

        public static async Task<long> MeasureValid(IServiceProvider root, bool validated, int iterations)
        {
            using IServiceScope scope = root.CreateScope();
            RogueDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<RogueDispatcher>();
            CreateOrder order = Order(""R-1"", ""Ada"", ""A"", ""B"", ""C"");
            PlainOrder plain = new PlainOrder(""A"");
            long before = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < iterations; i++)
                {
                    ValueTask<int> vt = validated ? dispatcher.SendCreateOrder(order) : dispatcher.SendPlainOrder(plain);
                    if (!vt.IsCompletedSuccessfully)
                    {
                        throw new InvalidOperationException(""Expected the valid path to complete synchronously."");
                    }
                    _ = await vt;
                }
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
";

    // ── Rule attributes, nested and collection paths ─────────────────────────────────

    [Theory]
    [InlineData("valid")]
    [InlineData("no-lines")]
    [InlineData("concrete-valid")]
    public async Task ValidRequest_ReachesTheHandler(string scenario)
    {
        Outcome outcome = await SendAsync(scenario);

        Assert.Null(outcome.Thrown);
        Assert.Equal(1, outcome.Handled);
    }

    [Theory]
    [InlineData("reference-empty", "Reference")]
    [InlineData("customer-name-empty", "Customer.Name")]
    [InlineData("only-line-empty", "Lines[0].Sku")]
    [InlineData("third-line-empty", "Lines[2].Sku")]
    [InlineData("first-and-third-line-empty", "Lines[0].Sku", "Lines[2].Sku")]
    [InlineData("reference-and-line-empty", "Reference", "Lines[1].Sku")]
    public async Task BrokenAttributeRule_ThrowsBeforeTheHandler_WithEveryFailingPath(string scenario, params string[] paths)
    {
        Outcome outcome = await SendAsync(scenario);

        AssertRejected(outcome);
        Assert.Equal(paths.OrderBy(static p => p, StringComparer.Ordinal), outcome.Keys);
        Assert.All(outcome.Problems!, static p => AssertHasMessages(p.Value));
    }

    [Fact]
    public async Task BrokenRule_ThroughTheConcreteDispatchPath_AlsoThrows()
    {
        Outcome outcome = await SendAsync("concrete-reference-empty");

        AssertRejected(outcome);
        Assert.Equal(new[] { "Reference" }, outcome.Keys);
    }

    [Theory]
    [InlineData("archive-valid", false)]
    [InlineData("archive-empty", true)]
    [InlineData("find-valid", false)]
    [InlineData("find-empty", true)]
    public async Task VoidCommandsAndQueries_AreValidatedToo(string scenario, bool rejected)
    {
        Outcome outcome = await SendAsync(scenario);

        if (rejected)
        {
            AssertRejected(outcome);
            Assert.Equal(new[] { "Reference" }, outcome.Keys);
        }
        else
        {
            Assert.Null(outcome.Thrown);
            Assert.Equal(1, outcome.Handled);
        }
    }

    // ── Rules hook ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RulesHook_Error_ThrowsBeforeTheHandler_AndGroupsMessagesByPath()
    {
        Outcome outcome = await SendAsync("window-reversed");

        AssertRejected(outcome);
        KeyValuePair<string, string[]> window = Assert.Single(outcome.Problems!);
        Assert.Equal("Window", window.Key);
        Assert.Equal(
            new[] { "The window start is out of range.", "The window starts after it ends." },
            window.Value.OrderBy(static m => m, StringComparer.Ordinal));
    }

    [Fact]
    public async Task RulesHook_WithNoError_LetsTheRequestThrough()
    {
        Outcome outcome = await SendAsync("window-ordered");

        Assert.Null(outcome.Thrown);
        Assert.Equal(1, outcome.Handled);
    }

    // ── IAsyncRules<T> from DI ───────────────────────────────────────────────────────

    [Fact]
    public async Task AsyncRules_ResolvedFromDi_RejectTheRequest()
    {
        Outcome outcome = await SendAsync("reserve-taken");

        AssertRejected(outcome);
        KeyValuePair<string, string[]> reference = Assert.Single(outcome.Problems!);
        Assert.Equal("Reference", reference.Key);
        Assert.Contains("The reference is already used.", reference.Value);
    }

    [Fact]
    public async Task AsyncRules_WithNoError_LetTheRequestThrough()
    {
        Outcome outcome = await SendAsync("reserve-free");

        Assert.Null(outcome.Thrown);
        Assert.Equal(1, outcome.Handled);
    }

    // ── 0 B on the valid path ────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidPath_AllocatesNothing()
    {
        const int Iterations = 1000;
        IServiceProvider provider = _consumer.Value.BuildProvider(ServiceLifetime.Singleton);

        long plain = await MeasureAsync(provider, validated: false, Iterations);
        long validated = await MeasureAsync(provider, validated: true, Iterations);

        Assert.True(plain == 0, "The measurement is off: the unmarked request allocated " + plain + " B.");
        Assert.True(validated == 0, "The valid path of a validated request allocated " + validated + " B over " + Iterations + " sends.");
    }

    // ── Requests without [Validate] are generated exactly as before ─────────────────

    [Fact]
    public void UnmarkedRequest_WithThePackageReferenced_IsGeneratedAsBefore()
    {
        string before = Dispatcher(RunGenerator(PlainSource, withPackage: false));
        string after = Dispatcher(RunGenerator(PlainSource, withPackage: true));

        Assert.Equal(before, after);
    }

    [Fact]
    public void UnmarkedRequest_NextToValidatedRequests_IsGeneratedAsBefore()
    {
        string before = SendMembers(Dispatcher(RunGenerator(PlainSource, withPackage: false)), "Send_App_PlainOrder");
        string after = SendMembers(Dispatcher(_consumer.Value.Generated), "Send_App_PlainOrder");

        Assert.NotEmpty(before);
        Assert.Equal(before, after);
    }

    // ── Project conventions the package keeps ───────────────────────────────────────

    [Fact]
    public void GeneratedValidationCode_UsesNoReflection()
    {
        foreach (GeneratedSourceResult source in _consumer.Value.Generated.Results.SelectMany(static r => r.GeneratedSources))
        {
            AssertNoReflection(source.HintName, source.SourceText.ToString());
        }
    }

    [Fact]
    public void PackageSource_UsesNoReflection()
    {
        string[] files = PackageSourceFiles();

        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            AssertNoReflection(file, File.ReadAllText(file));
        }
    }

    [Fact]
    public void Package_TargetsNet8AndNet10Only()
    {
        string project = File.ReadAllText(PackageFile(PackageName + ".csproj"));
        Match tfms = Regex.Match(project, @"<TargetFrameworks?>([^<]*)</TargetFrameworks?>");

        Assert.True(tfms.Success, "The package project declares no target framework.");
        string[] targets = tfms.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(new[] { "net10.0", "net8.0" }, targets.OrderBy(static t => t, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("ValidateAttribute")]
    [InlineData("ValidationErrors")]
    [InlineData("IAsyncRules<T>")]
    [InlineData("RogueValidationException")]
    [InlineData("ToProblemDictionary")]
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
    public void Solution_KeepsTheFluentValidationPackageAlongside()
    {
        string solution = File.ReadAllText(Path.Combine(RepoRoot(), "SkathIO.Rogue.sln"));

        Assert.Contains(@"src\SkathIO.Rogue.Validation\SkathIO.Rogue.Validation.csproj", solution, StringComparison.Ordinal);
        Assert.Contains(@"src\SkathIO.Rogue.Validation.FluentValidation\SkathIO.Rogue.Validation.FluentValidation.csproj", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void Changelog_Unreleased_DescribesThePackage()
    {
        string changelog = File.ReadAllText(Path.Combine(RepoRoot(), "CHANGELOG.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        Match unreleased = Regex.Match(changelog, @"^## \[Unreleased\]\n(.*?)(?=^## \[)", RegexOptions.Multiline | RegexOptions.Singleline);

        Assert.True(unreleased.Success, "CHANGELOG.md has no [Unreleased] section.");
        Assert.Contains("RogueValidationException", unreleased.Groups[1].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryGeneratorDiagnostic_HasAnAnalyzerReleaseRow()
    {
        string generatorDir = Path.Combine(RepoRoot(), "src", "SkathIO.Rogue.SourceGenerator");
        string descriptors = File.ReadAllText(Path.Combine(generatorDir, "DiagnosticDescriptors.cs"));
        string releases = string.Concat(Directory.GetFiles(generatorDir, "AnalyzerReleases.*.md").Select(File.ReadAllText));

        string[] ids = Regex.Matches(descriptors, @"id:\s*""(ROGUE\d{3})""").Select(static m => m.Groups[1].Value).ToArray();
        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.Matches(new Regex("^" + id + @"\s*\|\s*SkathIO\.Rogue\s*\|\s*(Error|Warning|Info)\s*\|\s*\S", RegexOptions.Multiline), releases));
    }

    [Fact]
    public void Generator_StaysOnNetstandard20_WithRoslynPinnedAt500()
    {
        string generator = File.ReadAllText(Path.Combine(RepoRoot(), "src", "SkathIO.Rogue.SourceGenerator", "SkathIO.Rogue.SourceGenerator.csproj"));
        string packages = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Packages.props"));

        Assert.Matches(@"<TargetFramework>\s*netstandard2\.0\s*</TargetFramework>", generator);
        Assert.Matches(@"<PackageVersion\s+Include=""Microsoft\.CodeAnalysis\.CSharp""\s+Version=""5\.0\.0""\s*/>", packages);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private sealed record Outcome(int Handled, string? Thrown, KeyValuePair<string, string[]>[]? Problems)
    {
        public string[] Keys => (Problems ?? Array.Empty<KeyValuePair<string, string[]>>())
            .Select(static p => p.Key)
            .OrderBy(static k => k, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<Outcome> SendAsync(string scenario)
    {
        Consumer consumer = _consumer.Value;
        MethodInfo send = consumer.Probe.GetMethod("Send")!;
        var task = (Task<(int, string?, KeyValuePair<string, string[]>[]?)>)send.Invoke(
            null, new object[] { consumer.BuildProvider(ServiceLifetime.Transient), scenario })!;
        (int handled, string? thrown, KeyValuePair<string, string[]>[]? problems) = await task;
        return new Outcome(handled, thrown, problems);
    }

    private static async Task<long> MeasureAsync(IServiceProvider provider, bool validated, int iterations)
    {
        MethodInfo measure = _consumer.Value.Probe.GetMethod("MeasureValid")!;
        return await (Task<long>)measure.Invoke(null, new object[] { provider, validated, iterations })!;
    }

    private static void AssertRejected(Outcome outcome)
    {
        Assert.Equal("RogueValidationException", outcome.Thrown);
        Assert.Equal(0, outcome.Handled);
        Assert.NotNull(outcome.Problems);
    }

    private static void AssertHasMessages(string[] messages)
    {
        Assert.NotEmpty(messages);
        Assert.All(messages, static m => Assert.False(string.IsNullOrWhiteSpace(m)));
    }

    private static void AssertNoReflection(string name, string text)
    {
        string[] banned =
        {
            "System.Reflection", "GetCustomAttribute", "Activator.CreateInstance", "MakeGenericType",
            "MakeGenericMethod", "GetProperties(", "GetProperty(", "GetMethod(", "Assembly.GetTypes",
            "AppDomain.CurrentDomain.GetAssemblies",
        };
        foreach (string api in banned)
        {
            Assert.False(text.Contains(api, StringComparison.Ordinal), name + " uses the reflection API " + api + ".");
        }
    }

    private static GeneratorDriverRunResult RunGenerator(string source, bool withPackage)
    {
        CSharpCompilation compilation = Compile("GeneratorInput", new[] { Parse(source) }, References(withPackage));
        GeneratorDriverRunResult result = GeneratorTestHelper.CreateDriver().RunGenerators(compilation).GetRunResult();
        Assert.Empty(result.Results.Where(static r => r.Exception is not null));
        return result;
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

    private static SyntaxTree Parse(string source) =>
        CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));

    private static CSharpCompilation Compile(string name, IEnumerable<SyntaxTree> trees, IEnumerable<MetadataReference> references) =>
        CSharpCompilation.Create(
            name,
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static readonly string[] _baseAssemblies =
    {
        "System.Private.CoreLib", "System.Runtime", "System.Collections", "System.Threading.Tasks", "System.Linq",
        "netstandard", "System.ComponentModel", "System.Diagnostics.DiagnosticSource",
        "Microsoft.Extensions.DependencyInjection.Abstractions", "SkathIO.Rogue.Abstractions", "SkathIO.Rogue",
    };

    // The assemblies a consumer of the core package compiles against, plus (when asked) the built
    // package and what it references; without it, every trace of the package is left out so the
    // "before" run sees no feature module at all.
    private static MetadataReference[] References(bool withPackage)
    {
        Dictionary<string, string> host = HostAssemblies();
        var names = new HashSet<string>(_baseAssemblies, StringComparer.Ordinal);
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

    // The package assembly built for this test run's configuration and framework, or the copy the
    // host already loads when a project reference brings it in.
    private static string PackageAssemblyPath()
    {
        if (HostAssemblies().TryGetValue(PackageName, out string? hosted))
        {
            return hosted;
        }

        var testDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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

    // The validated consumer compiled once, with the generator run that produced it and the
    // registrars its module initializer added to the process-wide registration bridge.
    private sealed class Consumer
    {
        private Consumer(GeneratorDriverRunResult generated, Type probe, Action<IServiceCollection, RogueOptions>[] registrars)
        {
            Generated = generated;
            Probe = probe;
            _registrars = registrars;
        }

        private readonly Action<IServiceCollection, RogueOptions>[] _registrars;

        public GeneratorDriverRunResult Generated { get; }

        public Type Probe { get; }

        public static Consumer Build()
        {
            GeneratorDriverRunResult generated = RunGenerator(ValidatedSource, withPackage: true);
            Assert.DoesNotContain(generated.Results.SelectMany(static r => r.Diagnostics), static d => d.Severity == DiagnosticSeverity.Error);

            Assembly assembly = Emit(generated);
            Type probe = assembly.GetType("App.Probe", throwOnError: true)!;
            return new Consumer(generated, probe, CaptureRegistrars(assembly));
        }

        public IServiceProvider BuildProvider(ServiceLifetime handlerLifetime)
        {
            Action<IServiceCollection, RogueOptions>[] saved = RogueRegistrationBridge.SnapshotRegistrars();
            try
            {
                RogueRegistrationBridge.RestoreRegistrars(_registrars);
                var services = new ServiceCollection();
                services.AddRogue(o => o.Lifetime = handlerLifetime);
                Probe.GetMethod("Register")!.Invoke(null, new object[] { services });
                return services.BuildServiceProvider();
            }
            finally
            {
                RogueRegistrationBridge.RestoreRegistrars(saved);
            }
        }

        private static Assembly Emit(GeneratorDriverRunResult generated)
        {
            IEnumerable<SyntaxTree> trees = generated.Results
                .SelectMany(static r => r.GeneratedSources)
                .Select(static s => Parse(s.SourceText.ToString()))
                .Prepend(Parse(ValidatedSource));
            CSharpCompilation compilation = Compile("ValidationConsumer_" + Guid.NewGuid().ToString("N"), trees, References(withPackage: true));

            using var image = new MemoryStream();
            var emit = compilation.Emit(image);
            Assert.True(
                emit.Success,
                "The validated consumer failed to compile:\n" +
                string.Join("\n", emit.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error)));

            string packagePath = PackageAssemblyPath();
            AssemblyLoadContext.Default.Resolving += (context, name) =>
                string.Equals(name.Name, PackageName, StringComparison.Ordinal) ? context.LoadFromAssemblyPath(packagePath) : null;
            return Assembly.Load(image.ToArray());
        }

        // Runs the consumer's module initializer against an empty bridge and keeps what it added, so
        // each provider is built from this consumer's registrars alone.
        private static Action<IServiceCollection, RogueOptions>[] CaptureRegistrars(Assembly assembly)
        {
            Action<IServiceCollection, RogueOptions>[] saved = RogueRegistrationBridge.SnapshotRegistrars();
            try
            {
                RogueRegistrationBridge.RestoreRegistrars(Array.Empty<Action<IServiceCollection, RogueOptions>>());
                RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
                return RogueRegistrationBridge.SnapshotRegistrars();
            }
            finally
            {
                RogueRegistrationBridge.RestoreRegistrars(saved);
            }
        }
    }
}
