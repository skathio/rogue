using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The validation feature module beyond the package's headline rules: the build-error diagnostics for
/// misuse (ROGUE014-018), the shapes of properties it walks, rule overrides, null handling, the order in
/// which the rule steps run, and a [Validate] type validated across an assembly boundary.
/// </summary>
[Collection(RealDiDispatchCollection.Name)]
public sealed class ValidationModuleTests
{
    private const string Usings = @"
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkathIO.Rogue;
using SkathIO.Rogue.Validation;
";

    private static string Request(string body, string extra = "") => Usings + @"
namespace App
{
    [Validate]
    public sealed partial record Req : ICommand<int>
    {
" + body + @"
    }
    public sealed class ReqHandler : ICommandHandler<Req, int>
    {
        public static int Calls;
        public ValueTask<int> Handle(Req command, CancellationToken cancellationToken) { Calls++; return new ValueTask<int>(1); }
    }
" + extra + @"
}
";

    // ── Diagnostics ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Rogue014_NonPartialValidatedType_IsAnError()
    {
        string source = Usings + @"
namespace App
{
    [Validate] public sealed record Loose([property: NotEmpty] string Name);
    public class Outer { [Validate] public sealed partial record Inner([property: NotEmpty] string Name); }
}";
        Diagnostic[] found = Diagnostics(source, "ROGUE014");

        Assert.Equal(2, found.Length);
        Assert.All(found, static d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void Rogue015_RuleThatDoesNotFitThePropertyType_IsAnError()
    {
        string source = Request(@"
        [MaximumLength(3)] public int Count { get; init; }
        [Quirky] public string Name { get; init; } = ""x"";",
            @"
    public sealed class QuirkyAttribute : RuleAttribute
    {
        public static bool IsValid(string? value) => value is not null;
    }");

        Diagnostic[] found = Diagnostics(source, "ROGUE015");

        Assert.Equal(2, found.Length);
        Assert.Contains(found, static d => Message(d).Contains("App.Req.Count", StringComparison.Ordinal));
        Assert.Contains(found, static d => Message(d).Contains("DefaultCode", StringComparison.Ordinal));
    }

    [Fact]
    public void Rogue016_RulesMethodWithTheWrongSignature_IsAnError()
    {
        string source = Request(@"
        [NotEmpty] public string Name { get; init; } = ""x"";
        private static bool Rules(Req x) => true;");

        Diagnostic found = Assert.Single(Diagnostics(source, "ROGUE016"));

        Assert.Equal(DiagnosticSeverity.Error, found.Severity);
    }

    [Fact]
    public void Rogue017_ValidatedTypeFromAnAssemblyBuiltWithoutTheGenerator_IsAnError()
    {
        MetadataReference library = Library(withGenerator: false);
        string source = Request(@"public Lib.Line Item { get; init; } = new Lib.Line();");

        Diagnostic found = Assert.Single(Diagnostics(source, "ROGUE017", library));

        Assert.Contains("App.Req.Item", Message(found), StringComparison.Ordinal);
    }

    [Fact]
    public void Rogue018_RulesOnATypeWithoutValidate_AreAWarning()
    {
        string source = Usings + @"
namespace App
{
    public sealed record Unmarked { [NotEmpty] public string Name { get; init; } = ""x""; }
    public sealed record Plain;
    public sealed class PlainChecks : IAsyncRules<Plain>
    {
        public ValueTask CheckAsync(Plain request, ValidationErrors errors, CancellationToken cancellationToken) => default;
    }
}";
        Diagnostic[] found = Diagnostics(source, "ROGUE018");

        Assert.Equal(new[] { "App.Plain", "App.Unmarked" }, found.Select(static d => d.GetMessage().Split('\'')[1]).OrderBy(static n => n, StringComparer.Ordinal));
        Assert.All(found, static d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
    }

    [Fact]
    public void WellFormedRequest_RaisesNoValidationDiagnostic_AndCompiles()
    {
        Compile(Request(Shapes, ShapeTypes));
    }

    // ── Behaviour ────────────────────────────────────────────────────────────────────

    private const string Shapes = @"
        [NotEmpty(Code = ""ref.missing"", Message = ""Give a reference."")] public string Reference { get; init; } = ""x"";
        [MinimumLength(2), MaximumLength(4)] public string? Note { get; init; }
        [GreaterThan(0), LessThan(10)] public decimal Amount { get; init; } = 1;
        [GreaterThan(0)] public int? Optional { get; init; }
        [NotEmpty] public Guid Id { get; init; } = Guid.NewGuid();
        public Part[] Array { get; init; } = System.Array.Empty<Part>();
        public List<Part> List { get; init; } = new List<Part>();
        public IEnumerable<Part> Sequence { get; init; } = System.Array.Empty<Part>();
        public IList<Part?> Maybe { get; init; } = new List<Part?>();
        public IReadOnlyList<Part> Strict { get; init; } = System.Array.Empty<Part>();
        public Point? Where { get; init; }
        public Box<Part> Boxed { get; init; } = new Box<Part>();
        public static bool HookRan;
        private static void Rules(Req x, ref ValidationErrors errors) { HookRan = true; }";

    private const string ShapeTypes = @"
    [Validate] public sealed partial record Part([property: NotEmpty] string Sku);
    [Validate] public partial record struct Point { [GreaterThan(0)] public int X { get; init; } }
    [Validate] public sealed partial class Box<T> { [NotEmpty] public string Label { get; init; } = ""x""; }
    public static class Probe
    {
        public static async Task<string> Run(IServiceProvider services, Req request)
        {
            Req.HookRan = false;
            using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(services);
            var sender = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ISender>(scope.ServiceProvider);
            try
            {
                await sender.Send(request);
                return ""ok hook="" + Req.HookRan;
            }
            catch (RogueValidationException ex)
            {
                var parts = new List<string>();
                foreach (var e in ex.Errors) { parts.Add(e.Path + ""|"" + e.Code + ""|"" + e.Message); }
                return string.Join("";"", parts) + "" hook="" + Req.HookRan;
            }
        }

        public static Req Valid() => new Req();
        public static Req Overridden() => new Req { Reference = "" "" };
        public static Req Ranges() => new Req { Note = ""n"", Amount = 10, Optional = 0, Id = Guid.Empty };
        public static Req NullName() => new Req { Reference = null! };
        public static Req Items() => new Req
        {
            Array = new[] { new Part(""a""), new Part("""") },
            List = new List<Part> { new Part("""") },
            Sequence = new[] { new Part(""a""), new Part(""a""), new Part("""") },
            Maybe = new List<Part?> { null, new Part("""") },
            Strict = new Part[] { null! },
            Where = new Point { X = 0 },
            Boxed = new Box<Part> { Label = """" },
        };
    }";

    [Theory]
    [InlineData("Valid", "ok hook=True")]
    [InlineData("Overridden", "Reference|ref.missing|Give a reference. hook=False")]
    [InlineData("NullName", "Reference|not_null|'Reference' must not be null. hook=False")]
    public async Task Rules_ReportTheirCodeAndMessage_AndTheHookRunsOnlyWhenTheyPass(string scenario, string expected)
    {
        Assert.Equal(expected, await RunAsync(scenario));
    }

    [Fact]
    public async Task BuiltInRules_RejectOutOfRangeValues()
    {
        string outcome = await RunAsync("Ranges");

        Assert.Equal(
            new[] { "Amount|less_than", "Id|not_empty", "Note|minimum_length", "Optional|greater_than" },
            Codes(outcome));
    }

    [Fact]
    public async Task EveryCollectionShape_ReportsIndexedPaths_AndNullItems()
    {
        string outcome = await RunAsync("Items");

        Assert.Equal(
            new[]
            {
                "Array[1].Sku|not_empty", "Boxed.Label|not_empty", "List[0].Sku|not_empty", "Maybe[1].Sku|not_empty",
                "Sequence[2].Sku|not_empty", "Strict[0]|not_null", "Where.X|greater_than",
            },
            Codes(outcome));
    }

    [Fact]
    public async Task AsyncRules_DoNotRun_WhenTheSyncRulesFail()
    {
        string extra = @"
    public sealed class Checks : IAsyncRules<Req>
    {
        public static int Runs;
        public ValueTask CheckAsync(Req request, ValidationErrors errors, CancellationToken cancellationToken) { Runs++; return default; }
    }
    public static class Probe
    {
        public static async Task<string> Run(IServiceProvider services, string name)
        {
            using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(services);
            var sender = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ISender>(scope.ServiceProvider);
            try { await sender.Send(new Req { Name = name }); } catch (RogueValidationException) { }
            return Checks.Runs + ""/"" + ReqHandler.Calls;
        }
    }";
        Assembly assembly = Emit(Request(@"[NotEmpty] public string Name { get; init; } = ""x"";", extra));
        IServiceProvider provider = GeneratorTestHelper.BuildProviderFromGenerated(assembly);

        Assert.Equal("0/0", await Invoke(assembly, provider, ""));
        Assert.Equal("1/1", await Invoke(assembly, provider, "ok"));
    }

    [Fact]
    public async Task NestedTypeFromAnotherAssembly_BuiltWithTheGenerator_IsValidatedWithItsPath()
    {
        MetadataReference library = Library(withGenerator: true);
        string extra = @"
    public static class Probe
    {
        public static async Task<string> Run(IServiceProvider services, string sku)
        {
            using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(services);
            var sender = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ISender>(scope.ServiceProvider);
            try { await sender.Send(new Req { Item = new Lib.Line { Sku = sku } }); return ""ok""; }
            catch (RogueValidationException ex) { return ex.Errors[0].Path; }
        }
    }";
        Assembly assembly = Emit(Request(@"public Lib.Line Item { get; init; } = new Lib.Line();", extra), library);
        IServiceProvider provider = GeneratorTestHelper.BuildProviderFromGenerated(assembly);

        Assert.Equal("Item.Sku", await Invoke(assembly, provider, ""));
        Assert.Equal("ok", await Invoke(assembly, provider, "A-1"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    // The provider is built before any code of the assembly runs: its module initializer must run inside
    // BuildProviderFromGenerated, which captures the registrars it adds.
    private static readonly Lazy<(Assembly Assembly, IServiceProvider Provider)> _shapes = new Lazy<(Assembly, IServiceProvider)>(() =>
    {
        Assembly assembly = Emit(Request(Shapes, ShapeTypes));
        return (assembly, GeneratorTestHelper.BuildProviderFromGenerated(assembly));
    });

    private static async Task<string> RunAsync(string scenario)
    {
        (Assembly assembly, IServiceProvider provider) = _shapes.Value;
        Type probe = assembly.GetType("App.Probe", throwOnError: true)!;
        object request = probe.GetMethod(scenario)!.Invoke(null, null)!;
        return await Invoke(assembly, provider, request);
    }

    private static Task<string> Invoke(Assembly assembly, IServiceProvider provider, object argument) =>
        (Task<string>)assembly.GetType("App.Probe", throwOnError: true)!.GetMethod("Run")!.Invoke(null, new[] { provider, argument })!;

    // "path|code" pairs of a probe outcome, sorted, without the messages or the hook flag.
    private static string[] Codes(string outcome) => outcome.Split(" hook=")[0]
        .Split(';')
        .Select(static e => string.Join("|", e.Split('|').Take(2)))
        .OrderBy(static e => e, StringComparer.Ordinal)
        .ToArray();

    private static string Message(Diagnostic diagnostic) => diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    private static MetadataReference ValidationReference() =>
        MetadataReference.CreateFromFile(typeof(SkathIO.Rogue.Validation.ValidationErrors).Assembly.Location);

    private static MetadataReference[] References(params MetadataReference[] extra) =>
        GeneratorTestHelper.GetRuntimeReferences().Append(ValidationReference()).Concat(extra).ToArray();

    private static (Compilation Output, GeneratorDriverRunResult Run) Generate(string source, string name, params MetadataReference[] extra)
    {
        CSharpCompilation input = CSharpCompilation.Create(
            name,
            new[] { CSharpSyntaxTree.ParseText(source) },
            References(extra),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriverRunResult run = GeneratorTestHelper.CreateDriver()
            .RunGeneratorsAndUpdateCompilation(input, out Compilation output, out _)
            .GetRunResult();
        Assert.Empty(run.Results.Where(static r => r.Exception is not null));
        return (output, run);
    }

    private static Diagnostic[] Diagnostics(string source, string id, params MetadataReference[] extra) =>
        Generate(source, "Diagnostics", extra).Run.Results.SelectMany(static r => r.Diagnostics).Where(d => d.Id == id).ToArray();

    private static Compilation Compile(string source, params MetadataReference[] extra)
    {
        (Compilation output, GeneratorDriverRunResult run) = Generate(source, "Validated_" + Guid.NewGuid().ToString("N"), extra);
        Assert.DoesNotContain(run.Results.SelectMany(static r => r.Diagnostics), static d => d.Id.StartsWith("ROGUE", StringComparison.Ordinal) && d.Severity >= DiagnosticSeverity.Warning);
        Diagnostic[] errors = output.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, "The generated validation code failed to compile:\n" + string.Join("\n", errors.Select(static d => d.ToString())));
        return output;
    }

    private static Assembly Emit(string source, params MetadataReference[] extra)
    {
        using var image = new MemoryStream();
        Assert.True(Compile(source, extra).Emit(image).Success);
        return Assembly.Load(image.ToArray());
    }

    // A separate library holding a [Validate] type, compiled with or without the generator, and loaded
    // into the test process so a consumer built against it can run.
    private static MetadataReference Library(bool withGenerator)
    {
        const string source = Usings + @"
namespace Lib
{
    [Validate] public sealed partial record Line { [NotEmpty] public string Sku { get; init; } = ""x""; }
}";
        string name = "ValidationLibrary_" + Guid.NewGuid().ToString("N");
        Compilation library = withGenerator
            ? Generate(source, name).Output
            : CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(source) }, References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        Assert.True(library.Emit(image).Success);
        byte[] bytes = image.ToArray();
        Assembly loaded = Assembly.Load(bytes);
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, requested) => requested.Name == name ? loaded : null;
        return MetadataReference.CreateFromImage(bytes);
    }
}
