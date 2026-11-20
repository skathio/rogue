using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SkathIO.Rogue.Jobs;
using Xunit;

namespace SkathIO.Rogue.Generator.Tests;

/// <summary>
/// Scheduling in the jobs package: <c>IJobScheduler.Schedule(command, at)</c> stores a job that no
/// worker claims before <c>at</c>, under the same rules as an enqueue; a parameterless command marked
/// <c>[Recurring("cron")]</c> runs at each occurrence of its cron expression, parsed in-house, once per
/// occurrence even when two hosts share one store; a <c>[Job]</c> unique key makes a duplicate enqueue
/// return the existing job's id; and both database stores back these with a unique
/// <c>(task_key, run_at)</c> and a partial unique index. Consumer code is compiled and run here, with
/// the source generator, so each rule fails on its own until the API it names exists.
/// </summary>
[Collection(RealDiDispatchCollection.Name)]
public sealed class JobsSchedulingRulesTests
{
    private const int PayloadLimit = 64;

    private static readonly DateTimeOffset _start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<SchedulingConsumer> _delayed = new Lazy<SchedulingConsumer>(() => SchedulingConsumer.Compile(DelayedSource));
    private static readonly Lazy<SchedulingConsumer> _unique = new Lazy<SchedulingConsumer>(() => SchedulingConsumer.Compile(UniqueSource));
    private static readonly Lazy<SchedulingConsumer> _recurring = new Lazy<SchedulingConsumer>(() => SchedulingConsumer.Compile(RecurringSource));

    private delegate Task<Guid> ScheduleCall(IServiceProvider services, int id, string note, DateTimeOffset at);

    private delegate Task<Guid> EnqueueCall(IServiceProvider services, int id);

    private const string DelayedSource = @"
[Job] public sealed record Remind(int Id, string Note) : ICommand;
public sealed class RemindHandler : ICommandHandler<Remind>
{
    private readonly Action<string> _ran;
    public RemindHandler(Action<string> ran) { _ran = ran; }
    public ValueTask Handle(Remind command, CancellationToken cancellationToken) { _ran(""remind:"" + command.Id); return default; }
}
public sealed record Plain(int Id) : ICommand;
public sealed class PlainHandler : ICommandHandler<Plain>
{
    public ValueTask Handle(Plain command, CancellationToken cancellationToken) => default;
}
public static class Calls
{
    public static async Task<Guid> Schedule(IServiceProvider services, int id, string note, DateTimeOffset at)
    {
        Guid job = await services.GetRequiredService<IJobScheduler>().Schedule(new Remind(id, note), at);
        return job;
    }
    public static async Task<Guid> ScheduleNull(IServiceProvider services, int id, string note, DateTimeOffset at)
    {
        Guid job = await services.GetRequiredService<IJobScheduler>().Schedule<Remind>(null!, at);
        return job;
    }
    public static async Task<Guid> SchedulePlain(IServiceProvider services, int id, string note, DateTimeOffset at)
    {
        Guid job = await services.GetRequiredService<IJobScheduler>().Schedule(new Plain(id), at);
        return job;
    }
}";

    private const string UniqueSource = @"
[Job(UniqueKey = ""nightly-report"")] public sealed record Report(int Id) : ICommand;
public sealed class ReportHandler : ICommandHandler<Report>
{
    private readonly Action<string> _ran;
    public ReportHandler(Action<string> ran) { _ran = ran; }
    public ValueTask Handle(Report command, CancellationToken cancellationToken) { _ran(""report:"" + command.Id); return default; }
}
[Job] public sealed record Remind(int Id) : ICommand;
public sealed class RemindHandler : ICommandHandler<Remind>
{
    private readonly Action<string> _ran;
    public RemindHandler(Action<string> ran) { _ran = ran; }
    public ValueTask Handle(Remind command, CancellationToken cancellationToken) { _ran(""remind:"" + command.Id); return default; }
}
public static class Calls
{
    public static async Task<Guid> EnqueueReport(IServiceProvider services, int id)
    {
        Guid job = await services.GetRequiredService<IJobScheduler>().Enqueue(new Report(id));
        return job;
    }
    public static async Task<Guid> EnqueueRemind(IServiceProvider services, int id)
    {
        Guid job = await services.GetRequiredService<IJobScheduler>().Enqueue(new Remind(id));
        return job;
    }
}";

    // Minute 30 of every hour, in the five-field cron form.
    private const string RecurringSource = @"
[Job]
[Recurring(""30 * * * *"")]
public sealed record HalfPast : ICommand;
public sealed class HalfPastHandler : ICommandHandler<HalfPast>
{
    private readonly Action<string> _ran;
    public HalfPastHandler(Action<string> ran) { _ran = ran; }
    public ValueTask Handle(HalfPast command, CancellationToken cancellationToken) { _ran(""half-past""); return default; }
}";

    // ── Delayed jobs ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Schedule_StoresTheJobForItsTime_AndNoWorkerRunsItBefore()
    {
        var clock = new HardeningClock();
        var log = new SchedulingLog(clock);
        var store = new InMemoryJobStore(clock);
        await using ServiceProvider host = _delayed.Value.Host(log, store, worker: false);
        DateTimeOffset at = _start.AddHours(1);

        Guid id = await _delayed.Value.Call<ScheduleCall>("Schedule")(host, 1, "soon", at);
        JobSnapshot? stored = await store.GetAsync(id, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromTicks(1));
        int early = await host.GetRequiredService<JobWorker>().RunOnceAsync();

        Assert.NotNull(stored);
        Assert.Equal(JobState.Enqueued, stored.State);
        Assert.Equal(at, stored.RunAt);
        Assert.Equal(0, early);
        Assert.Empty(log.Names);
    }

    [Fact]
    public async Task Schedule_RunsTheJob_OnceItsTimeComes()
    {
        var clock = new HardeningClock();
        var log = new SchedulingLog(clock);
        await using ServiceProvider host = _delayed.Value.Host(log, new InMemoryJobStore(clock), worker: false);

        await _delayed.Value.Call<ScheduleCall>("Schedule")(host, 7, "soon", _start.AddHours(1));
        clock.Advance(TimeSpan.FromHours(1));
        int ran = await host.GetRequiredService<JobWorker>().RunOnceAsync();

        Assert.Equal(1, ran);
        Assert.Equal(new[] { "remind:7" }, log.Names);
    }

    [Fact]
    public async Task Schedule_OfACommandWithoutJob_IsRefused()
    {
        var clock = new HardeningClock();
        await using ServiceProvider host = _delayed.Value.Host(new SchedulingLog(clock), new InMemoryJobStore(clock), worker: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _delayed.Value.Call<ScheduleCall>("SchedulePlain")(host, 1, string.Empty, _start.AddHours(1)));
    }

    [Fact]
    public async Task Schedule_OfNull_IsRefused()
    {
        var clock = new HardeningClock();
        await using ServiceProvider host = _delayed.Value.Host(new SchedulingLog(clock), new InMemoryJobStore(clock), worker: false);

        await Assert.ThrowsAsync<ArgumentNullException>(() => _delayed.Value.Call<ScheduleCall>("ScheduleNull")(host, 1, string.Empty, _start.AddHours(1)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Schedule_KeepsThePayloadLimit(int bytesOverLimit, bool accepted)
    {
        var clock = new HardeningClock();
        var store = new InMemoryJobStore(clock);
        await using ServiceProvider host = _delayed.Value.Host(new SchedulingLog(clock), store, worker: false, o => o.MaxPayloadBytes = PayloadLimit);
        string note = new string('a', PayloadLimit - PayloadShape.Size(string.Empty) + bytesOverLimit);

        Task<Guid> scheduling = _delayed.Value.Call<ScheduleCall>("Schedule")(host, 1, note, _start.AddHours(1));

        if (accepted)
        {
            Assert.NotNull(await store.GetAsync(await scheduling, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => scheduling);
        }
    }

    // ── Unique keys ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Enqueue_OfADuplicateUniqueKey_ReturnsTheExistingId_AndRunsOnce()
    {
        var clock = new HardeningClock();
        var log = new SchedulingLog(clock);
        await using ServiceProvider host = _unique.Value.Host(log, new InMemoryJobStore(clock), worker: false);
        EnqueueCall enqueue = _unique.Value.Call<EnqueueCall>("EnqueueReport");

        Guid first = await enqueue(host, 1);
        Guid duplicate = await enqueue(host, 2);
        await DrainAsync(host);

        Assert.Equal(first, duplicate);
        Assert.Equal(new[] { "report:1" }, log.Names);
    }

    [Fact]
    public async Task Enqueue_WithoutAUniqueKey_StoresEveryJob()
    {
        var clock = new HardeningClock();
        var log = new SchedulingLog(clock);
        await using ServiceProvider host = _unique.Value.Host(log, new InMemoryJobStore(clock), worker: false);
        EnqueueCall enqueue = _unique.Value.Call<EnqueueCall>("EnqueueRemind");

        Guid first = await enqueue(host, 1);
        Guid second = await enqueue(host, 1);
        await DrainAsync(host);

        Assert.NotEqual(first, second);
        Assert.Equal(new[] { "remind:1", "remind:1" }, log.Names);
    }

    // ── Recurring jobs ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecurringJob_RunsAtEachOccurrenceOfItsCron_AndNotBefore()
    {
        var clock = new HardeningClock();
        var log = new SchedulingLog(clock);
        await using ServiceProvider host = _recurring.Value.Host(log, new InMemoryJobStore(clock), worker: true);
        IHostedService[] hosted = await StartAsync(host);

        await DriveAsync(clock, _start.AddMinutes(90), () => log.Count >= 1);
        await DriveAsync(clock, _start.AddMinutes(150), () => log.Count >= 2);
        await StopAsync(hosted);

        DateTimeOffset[] runs = log.Times;
        Assert.True(runs.Length >= 2, "The recurring job ran " + runs.Length + " times by " + clock.GetUtcNow().ToString("O") + ".");
        Assert.InRange(runs[0], _start.AddMinutes(30), _start.AddMinutes(90).AddTicks(-1));
        Assert.InRange(runs[1], _start.AddMinutes(90), _start.AddMinutes(150).AddTicks(-1));
    }

    [Fact]
    public async Task RecurringJob_WithTwoHostsOnOneStore_RunsOncePerOccurrence()
    {
        var clock = new HardeningClock();
        var log = new SchedulingLog(clock);
        var store = new InMemoryJobStore(clock);
        await using ServiceProvider first = _recurring.Value.Host(log, store, worker: true);
        await using ServiceProvider second = _recurring.Value.Host(log, store, worker: true);
        IHostedService[] hosted = (await StartAsync(first)).Concat(await StartAsync(second)).ToArray();

        await DriveAsync(clock, _start.AddMinutes(90), () => log.Count >= 1);
        await DriveAsync(clock, _start.AddMinutes(60), static () => false);
        await StopAsync(hosted);
        await DrainAsync(first);
        await DrainAsync(second);

        Assert.Equal(new[] { "half-past" }, log.Names);
        Assert.InRange(log.Times[0], _start.AddMinutes(30), _start.AddMinutes(90).AddTicks(-1));
    }

    [Fact]
    public void GeneratedRecurringCode_UsesNoReflectionOrExpressionTrees()
    {
        string[] banned =
        {
            "System.Reflection", "GetCustomAttribute", "Activator.CreateInstance", "MakeGenericType", "MakeGenericMethod",
            "GetMethod(", "MethodInfo", "System.Linq.Expressions", "Expression<", "Type.GetType(", "AssemblyQualifiedName",
        };

        string[] hits = SchedulingConsumer.Generate(RecurringSource)
            .SelectMany(s => banned.Where(b => s.Text.Contains(b, StringComparison.Ordinal)).Select(b => s.Name + ": " + b))
            .ToArray();

        Assert.True(hits.Length == 0, "Generated code for a recurring job uses:\n" + string.Join("\n", hits));
    }

    [Theory]
    [InlineData("src/SkathIO.Rogue.Jobs/SkathIO.Rogue.Jobs.csproj")]
    [InlineData("src/SkathIO.Rogue.Jobs.Postgres/SkathIO.Rogue.Jobs.Postgres.csproj")]
    [InlineData("src/SkathIO.Rogue.Jobs.SqlServer/SkathIO.Rogue.Jobs.SqlServer.csproj")]
    public void CronParsing_IsInHouse_NoCronOrSchedulerPackageInTheRestoreGraph(string project)
    {
        string[] taken = RestoreGraph(project).Where(static id => Regex.IsMatch(id, "cron|quartz|hangfire", RegexOptions.IgnoreCase)).ToArray();

        Assert.True(taken.Length == 0, project + " takes a cron or scheduler package: " + string.Join(", ", taken));
    }

    // ── Database stores ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("src/SkathIO.Rogue.Jobs.Postgres")]
    [InlineData("src/SkathIO.Rogue.Jobs.SqlServer")]
    public void DatabaseStore_DedupesRecurringRuns_WithAUniqueTaskKeyAndRunAt(string packageDir)
    {
        const string pattern = @"\bUNIQUE\b[^;]*?\(\s*(task_key\s*,\s*run_at|run_at\s*,\s*task_key)\s*\)";

        bool found = StoreSources(packageDir).Any(f => Regex.IsMatch(File.ReadAllText(f), pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline));

        Assert.True(found, "No source in " + packageDir + " makes (task_key, run_at) unique.");
    }

    [Theory]
    [InlineData("src/SkathIO.Rogue.Jobs.Postgres")]
    [InlineData("src/SkathIO.Rogue.Jobs.SqlServer")]
    public void DatabaseStore_KeepsUniqueKeys_UnderAPartialUniqueIndex(string packageDir)
    {
        var partialUnique = new Regex(@"\bCREATE\s+UNIQUE\s+(NONCLUSTERED\s+|CLUSTERED\s+)?INDEX\b[^;]*?\bWHERE\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // The ready-order index (queue, run_at, seq) and the recurring-run dedupe are other rules.
        string[] indexes = StoreSources(packageDir)
            .SelectMany(f => partialUnique.Matches(File.ReadAllText(f)).Select(static m => m.Value))
            .Where(static m => !Regex.IsMatch(m, @"\bseq\b|\btask_key\b", RegexOptions.IgnoreCase))
            .ToArray();

        Assert.True(indexes.Length > 0, "No partial unique index for unique keys in " + packageDir + ".");
    }

    private static async Task<IHostedService[]> StartAsync(IServiceProvider host)
    {
        IHostedService[] hosted = host.GetServices<IHostedService>().ToArray();
        foreach (IHostedService service in hosted)
        {
            await service.StartAsync(CancellationToken.None);
        }
        return hosted;
    }

    private static async Task StopAsync(IEnumerable<IHostedService> hosted)
    {
        foreach (IHostedService service in hosted)
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(HardeningRig.HangGuard);
        }
    }

    // Runs every job that is due now, until none is left.
    private static async Task DrainAsync(IServiceProvider host)
    {
        JobWorker worker = host.GetRequiredService<JobWorker>();
        for (int round = 0; round < 10 && await worker.RunOnceAsync() > 0; round++)
        {
        }
    }

    // Moves the clock in small steps until done or the cap, yielding between steps so the hosted
    // services see each moment; the hang guard only stops a run that would never finish.
    private static async Task DriveAsync(HardeningClock clock, DateTimeOffset cap, Func<bool> done)
    {
        using var guard = new CancellationTokenSource(HardeningRig.HangGuard);
        while (!done() && clock.GetUtcNow() < cap)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(1, guard.Token);
        }
    }

    private static string[] StoreSources(string packageDir)
    {
        string dir = Path.Combine(RepoFiles.Root, packageDir);
        Assert.True(Directory.Exists(dir), packageDir + " does not exist.");
        return Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(dir, "*.sql", SearchOption.AllDirectories))
            .Where(static f => !Regex.IsMatch(f.Replace('\\', '/'), "/(bin|obj)/"))
            .ToArray();
    }

    // The package ids in a project's restore graph, every target framework.
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

/// <summary>What the consumer's handlers ran, and when by the test clock.</summary>
internal sealed class SchedulingLog
{
    private readonly TimeProvider _clock;
    private readonly ConcurrentQueue<(string Name, DateTimeOffset At)> _runs = new ConcurrentQueue<(string Name, DateTimeOffset At)>();

    public SchedulingLog(TimeProvider clock) => _clock = clock;

    public TimeProvider Clock => _clock;

    public int Count => _runs.Count;

    public string[] Names => _runs.Select(static r => r.Name).ToArray();

    public DateTimeOffset[] Times => _runs.Select(static r => r.At).ToArray();

    public void Record(string name) => _runs.Enqueue((name, _clock.GetUtcNow()));
}

/// <summary>The JSON shape of the delayed consumer's command, to size a payload exactly.</summary>
internal sealed record PayloadShape(int Id, string Note)
{
    public static int Size(string note) => JsonSerializer.SerializeToUtf8Bytes(new PayloadShape(1, note), ResolvingJson.Resolving).Length;
}

/// <summary>
/// A serializer context for consumer types compiled at test time, which no JSON source generator saw;
/// it resolves their contracts at run time, which only a test may do.
/// </summary>
internal sealed class ResolvingJson : JsonSerializerContext
{
    public ResolvingJson()
        : base(null)
    {
    }

    public static JsonSerializerOptions Resolving { get; } = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    protected override JsonSerializerOptions? GeneratedSerializerOptions => null;

    public override JsonTypeInfo? GetTypeInfo(Type type) => Resolving.GetTypeInfo(type);
}

/// <summary>
/// Consumer code compiled with the Rogue source generator against the jobs package, loaded, and wired
/// into hosts as an application wires it: through the registration bridge and <c>AddRogue</c>.
/// </summary>
internal sealed class SchedulingConsumer
{
    private const string Usings = "using System;\nusing System.Threading;\nusing System.Threading.Tasks;\n"
        + "using Microsoft.Extensions.DependencyInjection;\nusing SkathIO.Rogue;\nusing SkathIO.Rogue.Jobs;\n\nnamespace Scheduling\n{\n";

    private readonly Assembly _assembly;
    private readonly Action<IServiceCollection, RogueOptions>[] _registrars;

    private SchedulingConsumer(Assembly assembly, Action<IServiceCollection, RogueOptions>[] registrars)
    {
        _assembly = assembly;
        _registrars = registrars;
    }

    public static SchedulingConsumer Compile(string body)
    {
        GeneratorTestHelper.CreateDriver().RunGeneratorsAndUpdateCompilation(Input(body), out Compilation output, out _);
        Diagnostic[] errors = output.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, "The consumer does not compile:\n" + string.Join("\n", errors.Select(static d => d.ToString())));

        using var image = new MemoryStream();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = output.Emit(image);
        Assert.True(emitted.Success, "The consumer does not emit:\n" + string.Join("\n", emitted.Diagnostics));
        Assembly assembly = Assembly.Load(image.ToArray());
        return new SchedulingConsumer(assembly, Registrars(assembly));
    }

    /// <summary>The generated sources for <paramref name="body"/>, by hint name.</summary>
    public static (string Name, string Text)[] Generate(string body)
    {
        GeneratorDriverRunResult result = GeneratorTestHelper.CreateDriver().RunGenerators(Input(body)).GetRunResult();
        Assert.Empty(result.Results.Where(static r => r.Exception is not null));
        return result.Results.SelectMany(static r => r.GeneratedSources).Select(static s => (s.HintName, s.SourceText.ToString())).ToArray();
    }

    public TDelegate Call<TDelegate>(string method)
        where TDelegate : Delegate
        => _assembly.GetType("Scheduling.Calls", throwOnError: true)!.GetMethod(method)!.CreateDelegate<TDelegate>();

    /// <summary>A host on the log's clock and <paramref name="store"/>, its handlers recording to <paramref name="log"/>, with the hosted worker when asked.</summary>
    public ServiceProvider Host(SchedulingLog log, IJobStore store, bool worker, Action<JobsOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(log.Clock);
        services.AddSingleton(store);
        services.AddSingleton<Action<string>>(log.Record);
        Action<IServiceCollection, RogueOptions>[] saved = RogueRegistrationBridge.SnapshotRegistrars();
        try
        {
            RogueRegistrationBridge.RestoreRegistrars(_registrars);
            services.AddRogue();
        }
        finally
        {
            RogueRegistrationBridge.RestoreRegistrars(saved);
        }
        services.AddRogueJobs(o =>
        {
            o.SerializerContext = new ResolvingJson();
            o.BackoffJitter = 0;
            o.PollInterval = TimeSpan.FromMilliseconds(10);
            configure?.Invoke(o);
        });
        if (worker)
        {
            services.AddRogueJobWorker();
        }
        return services.BuildServiceProvider();
    }

    // The registrars the consumer's module initializer adds, captured alone so each host gets them.
    private static Action<IServiceCollection, RogueOptions>[] Registrars(Assembly assembly)
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

    private static CSharpCompilation Input(string body) => CSharpCompilation.Create(
        "SchedulingConsumer" + Guid.NewGuid().ToString("N"),
        new[] { CSharpSyntaxTree.ParseText(Usings + body + "\n}\n", new CSharpParseOptions(LanguageVersion.Latest)) },
        References(),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

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
            .Append(typeof(ServiceCollection).Assembly.Location)
            .Concat(wanted)
            .DistinctBy(static p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(static p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    }
}
