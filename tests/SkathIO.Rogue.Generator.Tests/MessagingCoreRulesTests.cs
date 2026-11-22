using System;
using System.Collections.Concurrent;
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
/// Holds the SkathIO.Rogue.Messaging core package to its rules. A type marked <c>[Message("name")]</c>
/// travels under that stable name, never its CLR name, in an envelope that carries an id, the name, a
/// content type, correlation and causation ids and the W3C traceparent. <c>IMessageBus.Publish</c> and
/// <c>IPublisher.Publish</c> of a marked event send through the transport contract (batch send; receive
/// with ack, nack with a delay and dead-letter; capability flags), and the in-memory transport keeps it.
/// The consumer host dispatches each inbound name onto <c>IPublisher</c>/<c>ISender</c> through code the
/// core generator emits, retries a failing handler, bounds its concurrency, the message size (body
/// bytes, as <c>JobsOptions.MaxPayloadBytes</c> bounds a job) and the JSON depth (counted as
/// System.Text.Json counts nested objects and arrays), dead-letters an unknown name without running any
/// handler, and never logs a payload. Consumer code is compiled and run here against the built package,
/// so this file needs no compile-time reference to it and each rule fails on its own until it holds.
/// </summary>
[Collection(RealDiDispatchCollection.Name)]
public sealed class MessagingCoreRulesTests
{
    private const string PackageName = "SkathIO.Rogue.Messaging";
    private const string Secret = "s3cr3t";
    private const int SizeLimit = 64;
    private const int DepthLimit = 4;

    private static readonly int[] _defaults = { -1, -1, -1, -1 };
    private static readonly Lazy<MessagingConsumer> _consumer = new Lazy<MessagingConsumer>(() => MessagingConsumer.Compile(ConsumerSource));

    // Limits are passed as { MaxConcurrency, MaxAttempts, MaxMessageBytes, MaxJsonDepth }; -1 keeps the default.
    private const string ConsumerSource = @"
using System; using System.Collections.Concurrent; using System.Collections.Generic; using System.Diagnostics; using System.Linq; using System.Text;
using System.Text.Json.Serialization; using System.Threading; using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection; using Microsoft.Extensions.Logging; using SkathIO.Rogue; using SkathIO.Rogue.Messaging;
namespace Msg
{
    public sealed class Logs : ILoggerFactory, ILogger
    {
        private readonly ConcurrentQueue<string> _lines; public Logs(ConcurrentQueue<string> lines) { _lines = lines; }
        public ILogger CreateLogger(string category) => this; public void AddProvider(ILoggerProvider provider) { } public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull { _lines.Enqueue(state.ToString() ?? """"); return null; }
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.Select(p => p.Value) : Enumerable.Empty<object?>();
            _lines.Enqueue(formatter(state, ex) + "" "" + string.Join("" "", values) + "" "" + ex);
        }
    }
    [Message(""orders.created"")] public sealed record OrderCreated(int Id, string Note) : IEvent;
    [Message(""orders.ship"")] public sealed record ShipOrder(int Id, string Note) : ICommand;
    public sealed record Unnamed(int Id, string Note) : IEvent;
    public sealed class Probe
    {
        private readonly ConcurrentDictionary<int, int> _failed = new ConcurrentDictionary<int, int>();
        private int _active;
        public int Peak;
        public ConcurrentQueue<string> Runs { get; } = new ConcurrentQueue<string>();
        public async ValueTask Run(string kind, int id, string note)
        {
            int now = Interlocked.Increment(ref _active);
            lock (Runs) { Peak = Math.Max(Peak, now); }
            try
            {
                Runs.Enqueue(kind + "":"" + id);
                await Task.Delay(5);
                if (note.StartsWith(""fail"", StringComparison.Ordinal) || (note == ""once"" && _failed.TryAdd(id, 0))) { throw new InvalidOperationException(""handler failed""); }
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
    public sealed class OrderCreatedHandler : IEventHandler<OrderCreated>
    {
        private readonly Probe _p; public OrderCreatedHandler(Probe p) { _p = p; }
        public ValueTask Handle(OrderCreated e, CancellationToken ct) => _p.Run(""created"", e.Id, e.Note);
    }
    public sealed class ShipOrderHandler : ICommandHandler<ShipOrder>
    {
        private readonly Probe _p; public ShipOrderHandler(Probe p) { _p = p; }
        public ValueTask Handle(ShipOrder c, CancellationToken ct) => _p.Run(""ship"", c.Id, c.Note);
    }
    public sealed class UnnamedHandler : IEventHandler<Unnamed>
    {
        private readonly Probe _p; public UnnamedHandler(Probe p) { _p = p; }
        public ValueTask Handle(Unnamed e, CancellationToken ct) => _p.Run(""unnamed"", e.Id, e.Note);
    }
    public static class Calls
    {
        public static void Configure(IServiceCollection services, JsonSerializerContext json, TimeProvider clock, int[] limits, ConcurrentQueue<string> logs)
        {
            services.AddSingleton<ILoggerFactory>(new Logs(logs));
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddSingleton<IMessageTransport>(new InMemoryTransport(clock));
            services.AddSingleton<Probe>();
            services.AddRogueMessaging(o =>
            {
                o.SerializerContext = json;
                if (limits[0] >= 0) { o.MaxConcurrency = limits[0]; }
                if (limits[1] >= 0) { o.MaxAttempts = limits[1]; }
                if (limits[2] >= 0) { o.MaxMessageBytes = limits[2]; }
                if (limits[3] >= 0) { o.MaxJsonDepth = limits[3]; }
            });
        }
        private static InMemoryTransport T(IServiceProvider sp) => (InMemoryTransport)sp.GetRequiredService<IMessageTransport>();
        private static MessageEnvelope Env(string name, string json) => new MessageEnvelope { Name = name, ContentType = ""application/json"", Body = Encoding.UTF8.GetBytes(json) };
        public static async Task<string> Deliver(IServiceProvider sp, string name, string json, int count)
        {
            await T(sp).SendAsync(Enumerable.Range(0, count).Select(_ => Env(name, json)).ToArray(), CancellationToken.None);
            MessageConsumer consumer = sp.GetRequiredService<MessageConsumer>();
            for (int round = 0; round < 10; round++)
            {
                await consumer.RunOnceAsync();
                sp.GetRequiredService<Action<TimeSpan>>()(TimeSpan.FromDays(1));
            }
            Probe p = sp.GetRequiredService<Probe>();
            return ""runs="" + string.Join("","", p.Runs.OrderBy(r => r, StringComparer.Ordinal)) + "";dead="" + T(sp).DeadLettered.Count + "";peak="" + p.Peak;
        }
        public static async Task<string> Publish(IServiceProvider sp, string how)
        {
            using Activity activity = new Activity(""publish"").SetIdFormat(ActivityIdFormat.W3C).Start();
            using IServiceScope scope = sp.CreateScope();
            string outcome = ""sent"";
            try
            {
                if (how == ""bus"") { await scope.ServiceProvider.GetRequiredService<IMessageBus>().Publish(new OrderCreated(1, ""a"")); await scope.ServiceProvider.GetRequiredService<IMessageBus>().Publish(new OrderCreated(2, ""b"")); }
                else if (how == ""bus-unnamed"") { await scope.ServiceProvider.GetRequiredService<IMessageBus>().Publish(new Unnamed(1, ""a"")); }
                else if (how == ""publisher"") { await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new OrderCreated(3, ""c"")); }
                else { await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new Unnamed(4, ""d"")); }
            }
            catch (Exception) { outcome = ""refused""; }
            var sent = (await T(sp).ReceiveAsync(100, CancellationToken.None)).Select(m => m.Envelope);
            return outcome + ""\ntrace="" + activity.TraceId + ""\nruns="" + string.Join("","", sp.GetRequiredService<Probe>().Runs)
                + string.Concat(sent.Select(e => ""\n"" + e.Name + ""|"" + e.Id + ""|"" + e.ContentType + ""|"" + e.TraceParent));
        }
        public static async Task<string> Contract(IServiceProvider sp, string step)
        {
            InMemoryTransport t = T(sp); Action<TimeSpan> advance = sp.GetRequiredService<Action<TimeSpan>>(); CancellationToken none = CancellationToken.None;
            int count = step == ""batch0"" ? 0 : step == ""batch1"" ? 1 : 3;
            await t.SendAsync(Enumerable.Range(0, count).Select(_ => Env(""orders.created"", ""{}"")).ToArray(), none);
            var first = await t.ReceiveAsync(step == ""max2"" ? 2 : 10, none);
            foreach (var m in first)
            {
                if (step == ""ack"") { await t.AckAsync(m, none); }
                if (step == ""nack"") { await t.NackAsync(m, TimeSpan.FromSeconds(30), none); }
                if (step == ""dead"") { await t.DeadLetterAsync(m, ""test"", none); }
            }
            advance(TimeSpan.FromSeconds(29));
            int early = (await t.ReceiveAsync(10, none)).Count;
            advance(step == ""nack"" ? TimeSpan.FromSeconds(1) : TimeSpan.FromDays(1));
            int later = (await t.ReceiveAsync(10, none)).Count;
            return ""received="" + first.Count + "";early="" + early + "";later="" + later + "";dead="" + t.DeadLettered.Count;
        }
    }
}";

    // ── Outbound: stable names, the envelope, auto-forwarding ─────────────────────────

    [Fact]
    public async Task BusPublish_SendsUnderTheStableName_InAStampedEnvelope()
    {
        string[] lines = (await Publish("bus")).Split('\n');
        string[][] sent = lines.Skip(3).Select(static l => l.Split('|')).ToArray();
        string trace = lines[1].Substring("trace=".Length);

        Assert.Equal("sent", lines[0]);
        Assert.Equal(2, sent.Length);
        Assert.All(sent, static e => Assert.Equal("orders.created", e[0]));
        Assert.All(sent, static e => Assert.False(string.IsNullOrWhiteSpace(e[1]), "An envelope has no id."));
        Assert.NotEqual(sent[0][1], sent[1][1]);
        Assert.All(sent, static e => Assert.False(string.IsNullOrWhiteSpace(e[2]), "An envelope has no content type."));
        Assert.All(sent, e => Assert.StartsWith("00-" + trace + "-", e[3], StringComparison.Ordinal));
    }

    [Fact]
    public async Task BusPublish_OfATypeWithoutMessage_IsRefused_AndSendsNothing()
    {
        string[] lines = (await Publish("bus-unnamed")).Split('\n');

        Assert.Equal("refused", lines[0]);
        Assert.Equal(3, lines.Length);
    }

    [Theory]
    [InlineData("publisher", "created:3", "orders.created")]
    [InlineData("publisher-unnamed", "unnamed:4", null)]
    public async Task PublisherPublish_ForwardsOnlyMarkedEvents_AndStillRunsLocalHandlers(string how, string run, string? forwarded)
    {
        string[] lines = (await Publish(how)).Split('\n');

        Assert.Equal("runs=" + run, lines[2]);
        Assert.Equal(forwarded is null ? Array.Empty<string>() : new[] { forwarded }, lines.Skip(3).Select(static l => l.Split('|')[0]));
    }

    // ── Inbound: generated name-to-dispatch and the untrusted-input bounds ────────────

    [Theory]
    [InlineData("orders.created", "created:1")]
    [InlineData("orders.ship", "ship:1")]
    public async Task InboundMessage_ReachesTheHandlerForItsName(string name, string run)
    {
        Assert.Equal("runs=" + run + ";dead=0", Trim(await Deliver(_defaults, name, Body(30), 1)));
    }

    [Theory]
    [InlineData("orders.unknown")]
    [InlineData("Msg.OrderCreated")]
    [InlineData("OrderCreated")]
    public async Task InboundUnknownName_IsDeadLettered_WithoutRunningAnyHandler(string name)
    {
        Assert.Equal("runs=;dead=1", Trim(await Deliver(_defaults, name, Body(30), 1)));
    }

    [Theory]
    [InlineData(SizeLimit - 1, "runs=created:1;dead=0")]
    [InlineData(SizeLimit, "runs=created:1;dead=0")]
    [InlineData(SizeLimit + 1, "runs=;dead=1")]
    public async Task InboundMessageSize_IsBounded(int bytes, string expected)
    {
        Assert.Equal(expected, Trim(await Deliver(new[] { -1, -1, SizeLimit, -1 }, "orders.created", Body(bytes), 1)));
    }

    [Theory]
    [InlineData(DepthLimit, "runs=created:1;dead=0")]
    [InlineData(DepthLimit + 1, "runs=;dead=1")]
    public async Task InboundJsonDepth_IsBounded(int depth, string expected)
    {
        string json = "{\"Id\":1,\"Note\":\"n\",\"X\":" + new string('[', depth - 1) + "0" + new string(']', depth - 1) + "}";

        Assert.Equal(expected, Trim(await Deliver(new[] { -1, -1, -1, DepthLimit }, "orders.created", json, 1)));
    }

    // ── Consumer host: retries and bounded concurrency ────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task FailingHandler_RunsMaxAttemptsTimes_ThenIsDeadLettered(int attempts)
    {
        string expected = "runs=" + string.Join(",", Enumerable.Repeat("created:1", attempts)) + ";dead=1";

        Assert.Equal(expected, Trim(await Deliver(new[] { -1, attempts, -1, -1 }, "orders.created", Json("fail"), 1)));
    }

    [Fact]
    public async Task HandlerThatFailsOnce_IsRetried_AndNotDeadLettered()
    {
        Assert.Equal("runs=created:1,created:1;dead=0", Trim(await Deliver(new[] { -1, 3, -1, -1 }, "orders.created", Json("once"), 1)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ConsumerHost_RunsAtMostMaxConcurrencyHandlersAtOnce(int limit)
    {
        string result = await Deliver(new[] { limit, -1, -1, -1 }, "orders.created", Json("slow"), 6);
        int peak = int.Parse(result.Substring(result.LastIndexOf('=') + 1), System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains(";dead=0;", result, StringComparison.Ordinal);
        Assert.InRange(peak, 1, limit);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, true)]
    [InlineData(1, 1, false)]
    [InlineData(2, 0, true)]
    [InlineData(2, 1, false)]
    [InlineData(3, 0, true)]
    [InlineData(3, 1, false)]
    public async Task MessagingOption_BelowOne_IsRefused_AndOneIsAccepted(int option, int value, bool refused)
    {
        Assert.NotNull(_consumer.Value);
        int[] limits = (int[])_defaults.Clone();
        limits[option] = value;

        Exception? thrown = await Record.ExceptionAsync(() => Deliver(limits, "orders.created", Body(30), 1));

        Assert.True(refused == thrown is not null, "Option " + option + " = " + value + (refused ? " was accepted." : " was refused: " + thrown));
    }

    [Fact]
    public async Task Payloads_AreNeverLogged_ByDefault()
    {
        var logs = new ConcurrentQueue<string>();
        int[] limits = { -1, 2, SizeLimit, -1 };
        await _consumer.Value.Call(_consumer.Value.Host(limits, logs), "Deliver", "orders.created", Json("fail-" + Secret), 1);
        await _consumer.Value.Call(_consumer.Value.Host(limits, logs), "Deliver", "orders.unknown", Json(Secret), 1);
        await _consumer.Value.Call(_consumer.Value.Host(limits, logs), "Deliver", "orders.created", Json(Secret + new string('x', SizeLimit)), 1);
        await _consumer.Value.Call(_consumer.Value.Host(limits, logs), "Deliver", "orders.created", "{\"Id\":\"" + Secret + "\"", 1);

        Assert.DoesNotContain(logs, static l => l.Contains(Secret, StringComparison.Ordinal));
    }

    // ── Transport contract, on the in-memory transport ───────────────────────────────

    [Theory]
    [InlineData("batch0", "received=0;")]
    [InlineData("batch1", "received=1;")]
    [InlineData("batch3", "received=3;")]
    [InlineData("max2", "received=2;")]
    [InlineData("ack", "received=3;early=0;later=0;dead=0")]
    [InlineData("nack", "received=3;early=0;later=3;dead=0")]
    [InlineData("dead", "received=3;early=0;later=0;dead=3")]
    public async Task InMemoryTransport_KeepsTheTransportContract(string step, string expected)
    {
        string result = await _consumer.Value.Call(_consumer.Value.Host(_defaults, new ConcurrentQueue<string>()), "Contract", step);

        Assert.StartsWith(expected, result, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvelopeAndTransport_ExposeTheNamedMembers()
    {
        Assembly package = MessagingConsumer.Package();
        Type envelope = package.GetType(PackageName + ".MessageEnvelope", throwOnError: true)!;
        Type transport = package.GetType(PackageName + ".IMessageTransport", throwOnError: true)!;

        foreach (string field in new[] { "Id", "Name", "ContentType", "CorrelationId", "CausationId", "TraceParent" })
        {
            Assert.True(envelope.GetProperty(field) is not null, "MessageEnvelope has no " + field + ".");
        }
        Assert.NotNull(transport.GetProperty("Capabilities"));
    }

    // ── Generated code and package conventions ───────────────────────────────────────

    [Fact]
    public void GeneratedCode_MapsEachStableName_WithoutReflection()
    {
        string generated = string.Concat(_consumer.Value.Generated);

        Assert.Contains("\"orders.created\"", generated, StringComparison.Ordinal);
        Assert.Contains("\"orders.ship\"", generated, StringComparison.Ordinal);
        Assert.DoesNotMatch(_bannedApis, generated);
    }

    [Fact]
    public void PackageSource_UsesNoReflectionScanningOrBannedSerializer()
    {
        string[] files = Directory.GetFiles(Path.Combine(RepoFiles.Root, "src", PackageName), "*.cs", SearchOption.AllDirectories)
            .Where(static f => !Regex.IsMatch(f.Replace('\\', '/'), "/(bin|obj)/")).ToArray();

        Assert.NotEmpty(files);
        Assert.All(files, static f => Assert.DoesNotMatch(_bannedApis, File.ReadAllText(f)));
    }

    [Fact]
    public void Package_TargetsNet8AndNet10Only_AndIsInTheSolution()
    {
        string project = RepoFiles.Read(ProjectPath);

        Assert.Matches(@"<TargetFrameworks>\s*(net8\.0;net10\.0|net10\.0;net8\.0)\s*</TargetFrameworks>", project);
        Assert.Contains(ProjectPath.Replace('/', '\\'), RepoFiles.Read("SkathIO.Rogue.sln"), StringComparison.Ordinal);
        Assert.DoesNotMatch(@"Include=""(MediatR|Mediator\.)", project);
    }

    [Theory]
    [InlineData("SkathIO.Rogue.Messaging.MessageAttribute.MessageAttribute(string!")]
    [InlineData("SkathIO.Rogue.Messaging.IMessageBus.Publish")]
    [InlineData("SkathIO.Rogue.Messaging.IMessageTransport.SendAsync")]
    public void PublicApi_TracksTheMember_InTheFlatFilesAtTheProjectRoot(string member)
    {
        string root = Path.Combine(RepoFiles.Root, "src", PackageName);

        Assert.True(File.Exists(Path.Combine(root, "PublicAPI.Shipped.txt")), "PublicAPI.Shipped.txt is missing at the project root.");
        // Released entries move to PublicAPI.Shipped.txt, so the member counts as tracked in either file.
        string tracked = File.ReadAllText(Path.Combine(root, "PublicAPI.Unshipped.txt"))
            + File.ReadAllText(Path.Combine(root, "PublicAPI.Shipped.txt"));
        Assert.Contains(member, tracked, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreGraph_HoldsOnlyLicenseCheckedPackages()
    {
        string assets = Path.Combine(RepoFiles.Root, "src", PackageName, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), assets + " is missing; restore the solution first.");
        using System.Text.Json.JsonDocument graph = System.Text.Json.JsonDocument.Parse(File.ReadAllText(assets));
        HashSet<string> allowed = RepoFiles.Read(".github/allowed-packages.txt").Split('\n').Select(static l => l.Trim())
            .Where(static l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] unlisted = graph.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(static l => l.Value.GetProperty("type").GetString() == "package")
            .Select(static l => l.Name.Split('/')[0]).Where(id => !allowed.Contains(id)).ToArray();

        Assert.True(unlisted.Length == 0, "Not license-checked: " + string.Join(", ", unlisted));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private const string ProjectPath = "src/" + PackageName + "/" + PackageName + ".csproj";

    private static readonly Regex _bannedApis = new Regex(
        @"System\.Reflection|GetCustomAttribute|Activator\.CreateInstance|MakeGeneric(Type|Method)|Assembly\.GetTypes|GetAssemblies\(|"
        + @"System\.Linq\.Expressions|Expression<|Type\.GetType\(|AssemblyQualifiedName|BinaryFormatter|NetDataContractSerializer");

    private static string Json(string note) => "{\"Id\":1,\"Note\":\"" + note + "\"}";

    // An OrderCreated body of exactly this many UTF-8 bytes.
    private static string Body(int bytes) => Json(new string('x', bytes - Json(string.Empty).Length));

    private static string Trim(string result) => result.Substring(0, result.LastIndexOf(";peak=", StringComparison.Ordinal));

    private static Task<string> Deliver(int[] limits, string name, string json, int count) =>
        _consumer.Value.Call(_consumer.Value.Host(limits, new ConcurrentQueue<string>()), "Deliver", name, json, count);

    private static Task<string> Publish(string how) =>
        _consumer.Value.Call(_consumer.Value.Host(_defaults, new ConcurrentQueue<string>()), "Publish", how);
}

/// <summary>The consumer compiled with the core generator against the built messaging package, and hosts wired as an application wires it.</summary>
internal sealed class MessagingConsumer
{
    private static readonly Lazy<Assembly> _package = new Lazy<Assembly>(LoadPackage);
    private readonly Assembly _assembly;
    private readonly Action<IServiceCollection, RogueOptions>[] _registrars;

    private MessagingConsumer(Assembly assembly, string[] generated)
    {
        _assembly = assembly;
        Generated = generated;
        Action<IServiceCollection, RogueOptions>[] saved = RogueRegistrationBridge.SnapshotRegistrars();
        RogueRegistrationBridge.RestoreRegistrars(Array.Empty<Action<IServiceCollection, RogueOptions>>());
        RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        _registrars = RogueRegistrationBridge.SnapshotRegistrars();
        RogueRegistrationBridge.RestoreRegistrars(saved);
    }

    public string[] Generated { get; }

    public static Assembly Package() => _package.Value;

    public static MessagingConsumer Compile(string source)
    {
        string[] paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(static p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Append(Package().Location).ToArray();
        CSharpCompilation input = CSharpCompilation.Create("MessagingConsumer", new[] { CSharpSyntaxTree.ParseText(source) },
            paths.Select(static p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriverRunResult run = GeneratorTestHelper.CreateDriver().RunGeneratorsAndUpdateCompilation(input, out Compilation output, out _).GetRunResult();

        using var image = new MemoryStream();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = output.Emit(image);
        Assert.True(emitted.Success, "The consumer does not compile:\n" + string.Join("\n", emitted.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error)));
        image.Position = 0;
        string[] generated = run.Results.SelectMany(static r => r.GeneratedSources).Select(static s => s.SourceText.ToString()).ToArray();
        return new MessagingConsumer(AssemblyLoadContext.Default.LoadFromStream(image), generated);
    }

    public ServiceProvider Host(int[] limits, ConcurrentQueue<string> logs)
    {
        var clock = new HardeningClock();
        var services = new ServiceCollection();
        services.AddSingleton<Action<TimeSpan>>(clock.Advance);
        Action<IServiceCollection, RogueOptions>[] saved = RogueRegistrationBridge.SnapshotRegistrars();
        RogueRegistrationBridge.RestoreRegistrars(_registrars);
        services.AddRogue();
        RogueRegistrationBridge.RestoreRegistrars(saved);
        _assembly.GetType("Msg.Calls")!.GetMethod("Configure")!.Invoke(null, new object[] { services, new ResolvingJson(), clock, limits, logs });
        return services.BuildServiceProvider();
    }

    public async Task<string> Call(IServiceProvider host, string method, params object[] args)
    {
        try
        {
            return await (Task<string>)_assembly.GetType("Msg.Calls")!.GetMethod(method)!.Invoke(null, args.Prepend(host).ToArray())!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    // The package as the solution build left it, loaded once; the consumer binds to it by name.
    private static Assembly LoadPackage()
    {
        DirectoryInfo output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\'));
        string path = Path.Combine(RepoFiles.Root, "src", "SkathIO.Rogue.Messaging", "bin", output.Parent!.Name, output.Name, "SkathIO.Rogue.Messaging.dll");
        Assert.True(File.Exists(path), "The messaging package is not built: " + path + " is missing. Build the solution first.");
        Assembly package = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        AssemblyLoadContext.Default.Resolving += (_, name) => name.Name == package.GetName().Name ? package : null;
        return package;
    }
}
