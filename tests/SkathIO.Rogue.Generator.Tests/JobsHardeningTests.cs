using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkathIO.Rogue.Jobs;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// The job worker after the security review: every claimed job keeps its lease until it settles, on
/// each path that claims; a lease the store no longer holds is logged; the renewal loop outlives a
/// cancelled store call that is not the host stopping, and shutdown does not rethrow it; a retry delay
/// is never negative at any attempt count; and the worker runs as a hosted service. Jobs are registered
/// by hand here, as the generated code registers them, on a clock that moves only when a test says.
/// </summary>
public sealed class JobWorkerHardeningTests
{
    [Fact]
    public async Task RunOnce_KeepsTheLeaseOfAJobWaitingForASlot_UntilItSettles()
    {
        await using HardeningRig rig = HardeningRig.Create();
        IReadOnlyList<Guid> ids = await rig.Scheduler.EnqueueMany(new[] { new HoldJob(1), new HoldJob(2) });

        Task<int> running = rig.Worker.RunOnceAsync();
        await rig.Probe.Started.Task.WaitAsync(HardeningRig.HangGuard);
        await rig.RenewThroughAsync(rounds: 3);
        IReadOnlyList<ClaimedJob> stolen = await rig.Store.ClaimAsync("default", 10, HardeningRig.Lease, CancellationToken.None);
        rig.Probe.Open.TrySetResult();

        Assert.Equal(2, await running.WaitAsync(HardeningRig.HangGuard));
        Assert.Empty(stolen);
        Assert.Equal(new[] { 1, 2 }, rig.Probe.Ran.OrderBy(static id => id));
        Assert.Equal(JobState.Succeeded, await rig.StateAsync(ids[0]));
        Assert.Equal(JobState.Succeeded, await rig.StateAsync(ids[1]));
    }

    [Fact]
    public async Task RunAsync_KeepsARunningJobsLease_PastTheLeaseDuration()
    {
        await using HardeningRig rig = HardeningRig.Create();
        Task worker = rig.StartWorker();
        Guid id = await rig.Scheduler.Enqueue(new HoldJob(1));

        await rig.Probe.Started.Task.WaitAsync(HardeningRig.HangGuard);
        await rig.RenewThroughAsync(rounds: 3);
        IReadOnlyList<ClaimedJob> stolen = await rig.Store.ClaimAsync("default", 10, HardeningRig.Lease, CancellationToken.None);
        rig.Probe.Open.TrySetResult();
        await rig.Probe.Finished.Task.WaitAsync(HardeningRig.HangGuard);
        await rig.StopWorkerAsync(worker);

        Assert.Empty(stolen);
        Assert.Equal(JobState.Succeeded, await rig.StateAsync(id));
    }

    [Fact]
    public async Task Completion_ThatFindsTheLeaseLost_IsLogged()
    {
        await using HardeningRig rig = HardeningRig.Create();
        rig.Probe.Open.TrySetResult();
        await rig.Scheduler.Enqueue(new HoldJob(1));
        rig.Store.LoseLeases = true;

        Assert.Equal(1, await rig.Worker.RunOnceAsync().WaitAsync(HardeningRig.HangGuard));

        Assert.True(rig.Log.Warnings > 0, "The store held no lease for the completed job, and the worker logged nothing at Warning or above.");
    }

    [Fact]
    public async Task Renewal_ThatFindsALeaseLost_IsLogged()
    {
        await using HardeningRig rig = HardeningRig.Create();
        Task worker = rig.StartWorker();
        await rig.Scheduler.Enqueue(new HoldJob(1));
        await rig.Probe.Started.Task.WaitAsync(HardeningRig.HangGuard);

        rig.Store.LoseLeases = true;
        await rig.FireRenewalAsync();
        await rig.Log.Changed.WaitUntilAsync(() => rig.Log.Warnings > 0);
        await rig.StopWorkerAsync(worker);

        Assert.True(rig.Log.Warnings > 0);
    }

    [Fact]
    public async Task RenewalLoop_KeepsRenewing_AfterAStoreCallIsCancelledWithoutAStop()
    {
        await using HardeningRig rig = HardeningRig.Create();
        Task worker = rig.StartWorker();
        await rig.Scheduler.Enqueue(new HoldJob(1));
        await rig.Probe.Started.Task.WaitAsync(HardeningRig.HangGuard);

        rig.Store.CancelNextExtends(1);
        await rig.FireRenewalAsync();
        await rig.FireRenewalAsync();
        await rig.StopWorkerAsync(worker);

        Assert.True(rig.Store.ExtendCalls >= 2, "The renewal loop stopped after one cancelled store call.");
    }

    [Fact]
    public async Task RunAsync_ReturnsAtShutdown_WithoutRethrowingACancelledRenewal()
    {
        await using HardeningRig rig = HardeningRig.Create();
        rig.Store.CancelNextExtends(int.MaxValue);
        Task worker = rig.StartWorker();
        await rig.Scheduler.Enqueue(new HoldJob(1));
        await rig.Probe.Started.Task.WaitAsync(HardeningRig.HangGuard);

        await rig.FireRenewalAsync();
        Exception? error = await Record.ExceptionAsync(() => rig.StopWorkerAsync(worker));

        Assert.Null(error);
    }

    [Theory]
    [InlineData(1, 0, 1.0, 0)]
    [InlineData(1024, 0, 1.0, 0)]
    [InlineData(1025, 0, 1.0, 0)]
    [InlineData(1026, 0, 0.2, 0)]
    [InlineData(int.MaxValue - 1, 0, 1.0, 0)]
    [InlineData(1025, 2, 0.0, 60)]
    [InlineData(int.MaxValue - 1, 2, 0.0, 60)]
    public async Task RetryDelay_IsNeverNegative_AtAnyAttemptCount(int attempt, int firstSeconds, double jitter, int expectedSeconds)
    {
        await using HardeningRig rig = HardeningRig.Create(o =>
        {
            o.DefaultBackoff = TimeSpan.FromSeconds(firstSeconds);
            o.MaxBackoff = TimeSpan.FromMinutes(1);
            o.BackoffJitter = jitter;
        });
        rig.Store.Injected.Enqueue(HardeningRig.FailingClaim(attempt));

        await rig.Worker.RunOnceAsync().WaitAsync(HardeningRig.HangGuard);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Assert.Single(rig.Store.RetryDelays));
    }

    [Fact]
    public async Task AddRogueJobWorker_RegistersAHostedServiceFromThePackage_ThatRunsTheWorker()
    {
        Type? hostedType = Type.GetType("Microsoft.Extensions.Hosting.IHostedService, Microsoft.Extensions.Hosting.Abstractions");
        Assert.NotNull(hostedType);
        await using HardeningRig rig = HardeningRig.Create();
        // The worker, beside the recurring jobs' scheduler that the same call registers.
        object?[] services = rig.Services.GetServices(hostedType).ToArray();
        Assert.All(services, static s => Assert.Same(typeof(JobWorker).Assembly, s!.GetType().Assembly));
        object hosted = Assert.Single(services, static s => s!.GetType().Name == "JobWorkerService")!;
        rig.Probe.Open.TrySetResult();

        await InvokeAsync(hostedType, hosted, "StartAsync");
        await rig.Scheduler.Enqueue(new HoldJob(1));
        await rig.Probe.Finished.Task.WaitAsync(HardeningRig.HangGuard);
        await InvokeAsync(hostedType, hosted, "StopAsync");

        Assert.Same(typeof(JobWorker).Assembly, hosted.GetType().Assembly);
    }

    private static Task InvokeAsync(Type hostedType, object hosted, string method)
        => ((Task)hostedType.GetMethod(method)!.Invoke(hosted, new object[] { CancellationToken.None })!).WaitAsync(HardeningRig.HangGuard);
}

/// <summary>
/// The build-time serializer check and the runtime job registry agree: a command with a handler that
/// the project's <c>JsonSerializerContext</c> does not cover is either reported by the build, or does
/// not break resolution at startup. The consumer is compiled in memory with the core generator and the
/// repo's analyzers, then loaded and wired as an application would wire it.
/// </summary>
[Collection(RealDiDispatchCollection.Name)]
public sealed class JobCoverageAgreementTests
{
    private const string ContextName = "Coverage.CoverageJson";

    private static readonly Dictionary<string, string> _commands = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PlainCommand"] = "public sealed record PlainCommand(int Id) : ICommand;\n"
            + "public sealed class PlainCommandHandler : ICommandHandler<PlainCommand> { public ValueTask Handle(PlainCommand c, CancellationToken t) => default; }\n",
        ["PlainResultCommand"] = "public sealed record PlainResultCommand(int Id) : ICommand<int>;\n"
            + "public sealed class PlainResultCommandHandler : ICommandHandler<PlainResultCommand, int> { public ValueTask<int> Handle(PlainResultCommand c, CancellationToken t) => new ValueTask<int>(c.Id); }\n",
    };

    private static readonly Lazy<ImmutableArray<DiagnosticAnalyzer>> _analyzers = new Lazy<ImmutableArray<DiagnosticAnalyzer>>(LoadAnalyzers);

    [Theory]
    [InlineData("PlainCommand")]
    [InlineData("PlainResultCommand")]
    [InlineData("PlainCommand,PlainResultCommand")]
    public async Task UncoveredCommandWithoutJobAttribute_IsReportedAtBuild_OrResolvesAtRuntime(string commands)
    {
        string[] names = commands.Split(',');
        Compilation compilation = Generate(Consumer(names), out ImmutableArray<Diagnostic> generatorDiagnostics);
        Diagnostic[] build = generatorDiagnostics.AddRange(await AnalyzeAsync(compilation))
            .Where(static d => d.Severity >= DiagnosticSeverity.Warning)
            .ToArray();

        string[] unreported = names.Where(n => !build.Any(d => Names(d, n))).ToArray();
        Exception? startup = StartupError(compilation);

        Assert.True(unreported.Length == 0 || startup is null,
            "No build diagnostic names " + string.Join(", ", unreported) + ", yet resolving the scheduler fails: " + startup?.Message);
    }

    private static string Consumer(string[] names) =>
        "using System.Text.Json;\nusing System.Text.Json.Serialization;\nusing System.Text.Json.Serialization.Metadata;\n"
        + "using System.Threading;\nusing System.Threading.Tasks;\nusing SkathIO.Rogue;\n\nnamespace Coverage\n{\n"
        + string.Concat(names.Select(static n => _commands[n]))
        + "public partial class CoverageJson : JsonSerializerContext\n{\n"
        + "    public CoverageJson() : base(null) { }\n"
        + "    public static CoverageJson Instance { get; } = new CoverageJson();\n"
        + "    protected override JsonSerializerOptions? GeneratedSerializerOptions => null;\n"
        + "    public override JsonTypeInfo? GetTypeInfo(System.Type type) => null;\n}\n}\n";

    private static bool Names(Diagnostic diagnostic, string command)
    {
        string located = diagnostic.Location.IsInSource ? diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan) : string.Empty;
        return diagnostic.Id.StartsWith("ROGUE", StringComparison.Ordinal)
            && (diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains(command, StringComparison.Ordinal) || located.Contains(command, StringComparison.Ordinal));
    }

    private static Compilation Generate(string source, out ImmutableArray<Diagnostic> diagnostics)
    {
        CSharpCompilation input = CSharpCompilation.Create(
            "CoverageConsumer" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorTestHelper.CreateDriver().RunGeneratorsAndUpdateCompilation(input, out Compilation output, out diagnostics);

        Diagnostic[] errors = output.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, "The consumer does not compile:\n" + string.Join("\n", errors.Select(static d => d.ToString())));
        return output;
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(Compilation compilation)
    {
        ImmutableArray<Diagnostic> reported = await compilation.WithAnalyzers(_analyzers.Value).GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(reported, static d => d.Id == "AD0001");
        return reported;
    }

    // Loads the consumer and wires it through the registration bridge alone, as AddRogue does in an
    // application; returns why resolving the scheduler failed, or null when it resolved.
    private static Exception? StartupError(Compilation compilation)
    {
        Assembly consumer = Emit(compilation);
        Action<IServiceCollection, RogueOptions>[] saved = RogueRegistrationBridge.SnapshotRegistrars();
        try
        {
            RogueRegistrationBridge.RestoreRegistrars(Array.Empty<Action<IServiceCollection, RogueOptions>>());
            RuntimeHelpers.RunModuleConstructor(consumer.ManifestModule.ModuleHandle);
            var context = (JsonSerializerContext)consumer.GetType(ContextName, throwOnError: true)!.GetProperty("Instance")!.GetValue(null)!;
            var services = new ServiceCollection();
            services.AddRogue();
            services.AddRogueJobs(o => o.SerializerContext = context);
            using ServiceProvider provider = services.BuildServiceProvider();
            return Record.Exception(() => provider.GetRequiredService<IJobScheduler>());
        }
        finally
        {
            RogueRegistrationBridge.RestoreRegistrars(saved);
        }
    }

    private static Assembly Emit(Compilation compilation)
    {
        using var image = new MemoryStream();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(image);
        Assert.True(result.Success, "The consumer does not emit:\n" + string.Join("\n", result.Diagnostics));
        return Assembly.Load(image.ToArray());
    }

    // The core runtime references plus the jobs package and every assembly it references.
    private static MetadataReference[] References()
    {
        Dictionary<string, string> platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .DistinctBy(static p => Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal)
            .ToDictionary(static p => Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal);
        Assembly jobs = typeof(JobWorker).Assembly;
        IEnumerable<string> wanted = jobs.GetReferencedAssemblies().Select(static a => a.Name!)
            .Append("System.Text.Encodings.Web")
            .Append("System.Memory")
            .Where(platform.ContainsKey)
            .Select(n => platform[n]);
        return GeneratorTestHelper.GetRuntimeReferences().OfType<PortableExecutableReference>().Select(static r => r.FilePath!)
            .Append(jobs.Location)
            .Concat(wanted)
            .DistinctBy(static p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(static p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    }

    // Every analyzer in the Roslyn projects under src/, loaded into a context of their own that shares
    // Roslyn with the test host, so other tests that load the same assemblies are not disturbed.
    private static ImmutableArray<DiagnosticAnalyzer> LoadAnalyzers()
    {
        string configuration = Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('/', '\\')))!;
        var context = new AssemblyLoadContext("jobs-hardening-analyzers");
        return Directory.GetDirectories(Path.Combine(RepoFiles.Root, "src"))
            .Where(static dir => File.ReadAllText(Path.Combine(dir, Path.GetFileName(dir) + ".csproj")).Contains("\"Microsoft.CodeAnalysis.CSharp\"", StringComparison.Ordinal))
            .Select(dir => Path.Combine(dir, "bin", configuration, "netstandard2.0", Path.GetFileName(dir) + ".dll"))
            .Where(File.Exists)
            .SelectMany(path => context.LoadFromAssemblyPath(path).GetExportedTypes())
            .Where(static t => typeof(DiagnosticAnalyzer).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(static t => (DiagnosticAnalyzer)Activator.CreateInstance(t)!)
            .ToImmutableArray();
    }
}

/// <summary>
/// The jobs package takes Microsoft.Extensions.Hosting.Abstractions, pinned centrally and allow-listed
/// with its license; the license check covers every packable project; and the docs that count the
/// packable projects give the right count.
/// </summary>
public sealed class JobsPackagingHardeningTests
{
    private const string AllowListPath = ".github/allowed-packages.txt";

    private static readonly Regex _countPhrase = new Regex(
        @"\b(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen)\s+(packable\s+projects?|packages\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] _numberWords =
    {
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen",
    };

    [Fact]
    public void JobsPackage_ReferencesHostingAbstractions_WithItsVersionPinnedCentrally()
    {
        string project = RepoFiles.Read("src/SkathIO.Rogue.Jobs/SkathIO.Rogue.Jobs.csproj");
        string pins = RepoFiles.Read("Directory.Packages.props");

        Assert.Matches(@"<PackageReference\s+Include=""Microsoft\.Extensions\.Hosting\.Abstractions""", project);
        Assert.DoesNotMatch(@"<PackageReference\s+Include=""Microsoft\.Extensions\.Hosting\.Abstractions""[^>]*\bVersion\s*=", project);
        Assert.Matches(@"<PackageVersion\s+Include=""Microsoft\.Extensions\.Hosting\.Abstractions""\s+Version=""[^""]+""", pins);
    }

    [Fact]
    public void EveryPackageInTheJobsRestoreGraph_IsAllowListed_WithItsLicenseAnnotation()
    {
        Dictionary<string, string> allowed = AllowList();
        string[] graph = RestoreGraph("src/SkathIO.Rogue.Jobs/SkathIO.Rogue.Jobs.csproj");

        string[] unannotated = graph.Where(id => !allowed.TryGetValue(id, out string? note) || !IsLicenseNote(id, note)).ToArray();

        Assert.Contains("Microsoft.Extensions.Hosting.Abstractions", graph, StringComparer.OrdinalIgnoreCase);
        Assert.True(unannotated.Length == 0, "Not allow-listed with a '# <id> — <license> — verified <date>' note: " + string.Join(", ", unannotated));
    }

    [Fact]
    public void EveryPackableProjectsRestoreGraph_IsOnTheAllowList()
    {
        Dictionary<string, string> allowed = AllowList();

        string[] missing = RepoFiles.PackableProjects()
            .SelectMany(p => RestoreGraph(p).Where(id => !allowed.ContainsKey(id)).Select(id => p + ": " + id))
            .ToArray();

        Assert.True(missing.Length == 0, "The license check would fail on:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void LicenseCheck_ListsEveryPackableProject()
    {
        Match list = Regex.Match(RepoFiles.Read(".github/workflows/ci.yml"), @"packable_projects=\((?<list>.*?)\)", RegexOptions.Singleline);

        Assert.True(list.Success, "ci.yml has no packable_projects list.");
        Assert.All(RepoFiles.PackableProjects(), p => Assert.Contains("\"" + p + "\"", list.Groups["list"].Value, StringComparison.Ordinal));
        Assert.Contains("\"src/SkathIO.Rogue.Validation/SkathIO.Rogue.Validation.csproj\"", list.Groups["list"].Value, StringComparison.Ordinal);
        Assert.Contains("\"src/SkathIO.Rogue.Jobs/SkathIO.Rogue.Jobs.csproj\"", list.Groups["list"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowListHeader_NamesEveryPackableProject()
    {
        string header = string.Join("\n", File.ReadLines(Path.Combine(RepoFiles.Root, AllowListPath)).TakeWhile(static l => l.StartsWith('#')));

        Assert.All(RepoFiles.PackableProjects(), p => Assert.Contains(p, header, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("CLAUDE.md")]
    [InlineData("docs/governance.md")]
    [InlineData(AllowListPath)]
    public void StatedCountOfPackableProjects_MatchesTheProjects(string file)
    {
        int actual = RepoFiles.PackableProjects().Length;

        Assert.All(_countPhrase.Matches(RepoFiles.Read(file)), m =>
            Assert.True(ToNumber(m.Groups["n"].Value) == actual, file + " says '" + m.Value + "', but there are " + actual + " packable projects."));
    }

    private static int ToNumber(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            ? number
            : Array.FindIndex(_numberWords, w => w.Equals(text, StringComparison.OrdinalIgnoreCase));

    private static bool IsLicenseNote(string id, string note) =>
        note.StartsWith("# " + id + " — ", StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(note, @"verified \d{4}-\d{2}-\d{2}");

    // Each allow-listed id, with the comment lines directly above it.
    private static Dictionary<string, string> AllowList()
    {
        var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var comment = new List<string>();
        foreach (string line in File.ReadLines(Path.Combine(RepoFiles.Root, AllowListPath)).Select(static l => l.Trim()))
        {
            if (line.StartsWith('#'))
            {
                comment.Add(line);
                continue;
            }
            if (line.Length > 0)
            {
                notes[line] = string.Join("\n", comment);
            }
            comment.Clear();
        }
        return notes;
    }

    // The package ids in a project's restore graph, every target framework: what the license check lists.
    private static string[] RestoreGraph(string project)
    {
        string assets = Path.Combine(RepoFiles.Root, Path.GetDirectoryName(project)!, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), assets + " is missing; restore the solution first.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));
        return document.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(static l => l.Value.GetProperty("type").GetString() == "package")
            .Select(static l => l.Name.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

/// <summary>A command that runs until its probe opens.</summary>
public sealed record HoldJob(int Id) : ICommand;

/// <summary>A command whose handler always fails.</summary>
public sealed record FailJob(int Attempt) : ICommand;

[JsonSerializable(typeof(HoldJob))]
[JsonSerializable(typeof(FailJob))]
internal sealed partial class HardeningJson : JsonSerializerContext
{
}

/// <summary>Repo paths for the packaging checks.</summary>
internal static class RepoFiles
{
    private static readonly Lazy<string> _root = new Lazy<string>(FindRoot);

    public static string Root => _root.Value;

    public static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    /// <summary>Every project under src/ that sets IsPackable to true, as "src/X/X.csproj".</summary>
    public static string[] PackableProjects() =>
        Directory.GetFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(static p => Regex.IsMatch(File.ReadAllText(p), @"<IsPackable>\s*true\s*</IsPackable>", RegexOptions.IgnoreCase))
            .Select(static p => Path.GetRelativePath(Root, p).Replace('\\', '/'))
            .OrderBy(static p => p, StringComparer.Ordinal)
            .ToArray();

    private static string FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SkathIO.Rogue.sln")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd('/', '\\'));
        }
        Assert.NotNull(dir);
        return dir;
    }
}

/// <summary>A provider wired as an application wires the jobs package, around a recording store, log and clock.</summary>
internal sealed class HardeningRig : IAsyncDisposable
{
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _justUnderLease = Lease - TimeSpan.FromTicks(1);
    private readonly CancellationTokenSource _stopping = new CancellationTokenSource();

    private HardeningRig(ServiceProvider services)
    {
        Services = services;
        Clock = (HardeningClock)services.GetRequiredService<TimeProvider>();
        Store = (HardeningStore)services.GetRequiredService<IJobStore>();
        Log = (HardeningLog)services.GetRequiredService<ILoggerFactory>();
        Probe = services.GetRequiredService<HoldProbe>();
    }

    public ServiceProvider Services { get; }

    public HardeningClock Clock { get; }

    public HardeningStore Store { get; }

    public HardeningLog Log { get; }

    public HoldProbe Probe { get; }

    public JobWorker Worker => Services.GetRequiredService<JobWorker>();

    public IJobScheduler Scheduler => Services.GetRequiredService<IJobScheduler>();

    /// <summary>One worker slot, no jitter, a five-minute lease and a poll interval longer than any test.</summary>
    public static HardeningRig Create(Action<JobsOptions>? configure = null)
    {
        var clock = new HardeningClock();
        var probe = new HoldProbe();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IJobStore>(new HardeningStore(clock));
        services.AddSingleton<ILoggerFactory>(new HardeningLog());
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(probe);
        services.AddScoped<RogueDispatcher>();
        // Defaults other than JobDefaults.None mark both as [Job] commands, the only kind that runs.
        services.AddJobDefinition(new JobDefinition<HoldJob>("hardening.hold", new JobDefaults(null, 0, 0, 0), (_, job, token) => probe.RunAsync(job, token)));
        services.AddJobDefinition(new JobDefinition<FailJob>("hardening.fail", new JobDefaults(null, 0, 0, 0), static (_, _, _) => throw new InvalidOperationException("The job failed.")));
        services.AddRogueJobs(options =>
        {
            options.SerializerContext = HardeningJson.Default;
            options.MaxConcurrency = 1;
            options.BackoffJitter = 0;
            options.LeaseDuration = Lease;
            options.PollInterval = TimeSpan.FromDays(1);
            configure?.Invoke(options);
        });
        services.AddRogueJobWorker();
        return new HardeningRig(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }));
    }

    /// <summary>A claim of a failing job at <paramref name="attempt"/>, with attempts to spare.</summary>
    public static ClaimedJob FailingClaim(int attempt) => new ClaimedJob
    {
        Id = Guid.NewGuid(),
        Kind = "hardening.fail",
        Queue = "default",
        Payload = JsonSerializer.SerializeToUtf8Bytes(new FailJob(attempt), HardeningJson.Default.FailJob),
        Attempt = attempt,
        MaxAttempts = int.MaxValue,
    };

    public Task StartWorker() => Worker.RunAsync(_stopping.Token);

    public async Task StopWorkerAsync(Task worker)
    {
        await _stopping.CancelAsync();
        await worker.WaitAsync(HangGuard);
    }

    /// <summary>
    /// Waits until a timer would fire within the lease, moves the clock to just short of the lease, and
    /// waits for the renewal that brings.
    /// </summary>
    public async Task FireRenewalAsync()
    {
        await Clock.WaitForTimerWithinAsync(_justUnderLease);
        int before = Store.ExtendCalls;
        Clock.Advance(_justUnderLease);
        await Store.Changed.WaitUntilAsync(() => Store.ExtendCalls > before);
    }

    public async Task RenewThroughAsync(int rounds)
    {
        for (int round = 0; round < rounds; round++)
        {
            await FireRenewalAsync();
        }
    }

    public async Task<JobState> StateAsync(Guid id) => (await Store.GetAsync(id, CancellationToken.None))!.State;

    public async ValueTask DisposeAsync()
    {
        Probe.Open.TrySetResult();
        await _stopping.CancelAsync();
        _stopping.Dispose();
        await Services.DisposeAsync();
    }
}

/// <summary>Holds every <see cref="HoldJob"/> until a test opens it.</summary>
internal sealed class HoldProbe
{
    public TaskCompletionSource Started { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Open { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Finished { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<int> Ran { get; } = new ConcurrentQueue<int>();

    public async ValueTask RunAsync(HoldJob job, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        await Open.Task.WaitAsync(cancellationToken);
        Ran.Enqueue(job.Id);
        Finished.TrySetResult();
    }
}

/// <summary>Wakes waiters each time something they watch changes.</summary>
internal sealed class Notifier
{
    private readonly object _gate = new object();
    private TaskCompletionSource _next = NewSource();

    public void Notify()
    {
        TaskCompletionSource fired;
        lock (_gate)
        {
            fired = _next;
            _next = NewSource();
        }
        fired.TrySetResult();
    }

    /// <summary>Completes once <paramref name="condition"/> holds; fails after the hang guard.</summary>
    public Task WaitUntilAsync(Func<bool> condition) => WaitLoopAsync(condition).WaitAsync(HardeningRig.HangGuard);

    private async Task WaitLoopAsync(Func<bool> condition)
    {
        while (true)
        {
            Task next;
            lock (_gate)
            {
                next = _next.Task;
            }
            if (condition())
            {
                return;
            }
            await next;
        }
    }

    private static TaskCompletionSource NewSource() => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>An in-memory store that records retries and renewals, and can lose leases or cancel renewals on request.</summary>
internal sealed class HardeningStore : IJobStore
{
    private readonly InMemoryJobStore _inner;
    private int _extendCalls;
    private int _cancelledExtends;
    private int _loseLeases;

    public HardeningStore(TimeProvider clock) => _inner = new InMemoryJobStore(clock);

    public Notifier Changed { get; } = new Notifier();

    /// <summary>When set, the store reports every lease as no longer held, as after another worker took the job.</summary>
    public bool LoseLeases
    {
        get => Volatile.Read(ref _loseLeases) != 0;
        set => Volatile.Write(ref _loseLeases, value ? 1 : 0);
    }

    public int ExtendCalls => Volatile.Read(ref _extendCalls);

    /// <summary>Claims handed out before any stored job.</summary>
    public ConcurrentQueue<ClaimedJob> Injected { get; } = new ConcurrentQueue<ClaimedJob>();

    public ConcurrentQueue<TimeSpan> RetryDelays { get; } = new ConcurrentQueue<TimeSpan>();

    /// <summary>The next <paramref name="count"/> renewals fail as a store command timeout does.</summary>
    public void CancelNextExtends(int count) => Volatile.Write(ref _cancelledExtends, count);

    public ValueTask EnqueueAsync(IReadOnlyList<NewJob> jobs, CancellationToken cancellationToken) => _inner.EnqueueAsync(jobs, cancellationToken);

    public ValueTask<Guid> EnqueueOnceAsync(NewJob job, CancellationToken cancellationToken) => _inner.EnqueueOnceAsync(job, cancellationToken);

    public ValueTask<bool> TryAcquireLeadershipAsync(string name, string owner, TimeSpan lease, CancellationToken cancellationToken)
        => _inner.TryAcquireLeadershipAsync(name, owner, lease, cancellationToken);

    public ValueTask ReleaseLeadershipAsync(string name, string owner, CancellationToken cancellationToken) => _inner.ReleaseLeadershipAsync(name, owner, cancellationToken);

    public ValueTask<IReadOnlyList<ClaimedJob>> ClaimAsync(string queue, int maxCount, TimeSpan lease, CancellationToken cancellationToken)
    {
        var injected = new List<ClaimedJob>();
        while (injected.Count < maxCount && Injected.TryDequeue(out ClaimedJob? job))
        {
            injected.Add(job);
        }
        return injected.Count > 0 ? new ValueTask<IReadOnlyList<ClaimedJob>>(injected) : _inner.ClaimAsync(queue, maxCount, lease, cancellationToken);
    }

    public ValueTask<int> CompleteAsync(IReadOnlyList<JobLease> leases, CancellationToken cancellationToken)
        => LoseLeases ? new ValueTask<int>(0) : _inner.CompleteAsync(leases, cancellationToken);

    public ValueTask<bool> RetryAsync(JobLease lease, TimeSpan delay, string error, CancellationToken cancellationToken)
    {
        RetryDelays.Enqueue(delay);
        return new ValueTask<bool>(true);
    }

    public ValueTask<bool> DeadLetterAsync(JobLease lease, string error, CancellationToken cancellationToken) => _inner.DeadLetterAsync(lease, error, cancellationToken);

    public ValueTask<bool> ReleaseAsync(JobLease lease, TimeSpan delay, CancellationToken cancellationToken) => _inner.ReleaseAsync(lease, delay, cancellationToken);

    public ValueTask<int> RequeueAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken) => _inner.RequeueAsync(ids, cancellationToken);

    public async ValueTask<int> ExtendLeasesAsync(IReadOnlyList<JobLease> leases, TimeSpan lease, CancellationToken cancellationToken)
    {
        try
        {
            if (Interlocked.Decrement(ref _cancelledExtends) >= 0)
            {
                throw new OperationCanceledException("The store command timed out.");
            }
            return LoseLeases ? 0 : await _inner.ExtendLeasesAsync(leases, lease, cancellationToken);
        }
        finally
        {
            Interlocked.Increment(ref _extendCalls);
            Changed.Notify();
        }
    }

    public ValueTask<JobSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken) => _inner.GetAsync(id, cancellationToken);
}

/// <summary>Counts what the worker logs at Warning or above.</summary>
internal sealed class HardeningLog : ILoggerFactory
{
    private readonly ILogger _logger;
    private int _warnings;

    public HardeningLog()
    {
        _logger = DispatchProxy.Create<ILogger, WarningSink>();
        ((WarningSink)_logger).Log = this;
    }

    public Notifier Changed { get; } = new Notifier();

    public int Warnings => Volatile.Read(ref _warnings);

    public ILogger CreateLogger(string categoryName) => _logger;

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    public void Record(LogLevel level)
    {
        if (level >= LogLevel.Warning)
        {
            Interlocked.Increment(ref _warnings);
            Changed.Notify();
        }
    }
}

/// <summary>The logger behind <see cref="HardeningLog"/>: enabled at every level, passing each entry's level on.</summary>
internal class WarningSink : DispatchProxy
{
    public HardeningLog? Log { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(ILogger.Log))
        {
            Log!.Record((LogLevel)args![0]!);
        }
        return targetMethod?.Name == nameof(ILogger.IsEnabled) ? true : null;
    }
}

/// <summary>
/// A clock that moves only when a test advances it. Timers made from it fire during the advance that
/// reaches them, in due order, and a test can wait until a timer is armed.
/// </summary>
internal sealed class HardeningClock : TimeProvider
{
    private readonly object _gate = new object();
    private readonly List<ClockTimer> _timers = new List<ClockTimer>();
    private DateTimeOffset _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public Notifier Changed { get; } = new Notifier();

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, () => callback(state));
        lock (_gate)
        {
            _timers.Add(timer);
        }
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Completes once a timer is armed to fire within <paramref name="within"/> of now.</summary>
    public Task WaitForTimerWithinAsync(TimeSpan within) => Changed.WaitUntilAsync(() => NextDueBy(GetUtcNow() + within, move: false) is not null);

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target = GetUtcNow() + by;
        for (ClockTimer? due = NextDueBy(target, move: true); due is not null; due = NextDueBy(target, move: true))
        {
            due.Fire();
        }
    }

    // The earliest timer due by the target. Moving, the clock goes to that timer (rescheduled when
    // periodic), or to the target when none is due.
    private ClockTimer? NextDueBy(DateTimeOffset target, bool move)
    {
        lock (_gate)
        {
            ClockTimer? next = _timers.Where(t => t.DueAt <= target).MinBy(static t => t.DueAt);
            if (move)
            {
                _now = next?.DueAt ?? target;
                next?.Reschedule(_now);
            }
            return next;
        }
    }

    private sealed class ClockTimer : ITimer
    {
        private readonly HardeningClock _clock;
        private readonly Action _fire;
        private TimeSpan _period;

        public ClockTimer(HardeningClock clock, Action fire)
        {
            _clock = clock;
            _fire = fire;
        }

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_clock._gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _clock._now + dueTime;
                _period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            }
            _clock.Changed.Notify();
            return true;
        }

        public void Reschedule(DateTimeOffset now) => DueAt = _period > TimeSpan.Zero ? now + _period : null;

        public void Fire() => _fire();

        public void Dispose()
        {
            lock (_clock._gate)
            {
                _clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
