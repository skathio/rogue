using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Benchmarks;

/// <summary>
/// Scenario 4 — Publish a notification to N handlers (fan-out, N = 2, 5; all three libraries, PD-27).
/// Scenario 6 — NFR-PERF-5 honesty scenario (N = 20). Hypothesis A1: Rogue may not be the fastest
/// here because its generated <c>Publish_*</c> resolves handlers via a runtime
/// <c>GetServices&lt;INotificationHandler&lt;T&gt;&gt;()</c> DI enumeration on every call, whereas
/// martinothamar's generator emits a compile-time-fixed fan-out. See <c>bench/RESULTS.md</c>.
/// </summary>
/// <remarks>
/// Fan-out count N is fixed per <em>notification type</em> (see <c>BenchmarkHandlers.cs</c>): each of
/// the three libraries auto-discovers all handlers declared for a notification type, so N is encoded
/// in the type, not in selective DI registration. Rogue is the BenchmarkDotNet baseline.
/// </remarks>
[MemoryDiagnoser]
public class NotificationBenchmarks
{
    private IPublisher _rogue = null!;
    private global::MediatR.IPublisher _mediatR = null!;
    private global::Mediator.IMediator _mediator = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rogueServices = new ServiceCollection();
        rogueServices.AddRogue();
        // N=2
        rogueServices.AddTransient<INotificationHandler<PingNotificationN2>, PingNotificationN2Handler1>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN2>, PingNotificationN2Handler2>();
        // N=5
        rogueServices.AddTransient<INotificationHandler<PingNotificationN5>, PingNotificationN5Handler1>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN5>, PingNotificationN5Handler2>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN5>, PingNotificationN5Handler3>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN5>, PingNotificationN5Handler4>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN5>, PingNotificationN5Handler5>();
        // N=20
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler1>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler2>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler3>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler4>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler5>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler6>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler7>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler8>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler9>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler10>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler11>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler12>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler13>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler14>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler15>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler16>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler17>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler18>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler19>();
        rogueServices.AddTransient<INotificationHandler<PingNotificationN20>, PingNotificationN20Handler20>();
        _rogue = rogueServices.BuildServiceProvider().GetRequiredService<IPublisher>();

        var mediatRServices = new ServiceCollection();
        // RegisterServicesFromAssembly discovers every MediatR handler in this assembly (all N types).
        mediatRServices.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<MtrPingNotificationN2Handler1>());
        _mediatR = mediatRServices.BuildServiceProvider().GetRequiredService<global::MediatR.IPublisher>();

        // martinothamar: AddMediator() (generated) registers every discovered handler per type.
        var mediatorServices = new ServiceCollection();
        mediatorServices.AddMediator();
        _mediator = mediatorServices.BuildServiceProvider().GetRequiredService<global::Mediator.IMediator>();
    }

    // ── Scenario 4a — N = 2 ───────────────────────────────────────────────────────────────
    [Benchmark(Baseline = true)]
    public ValueTask Rogue_Publish_N2() => _rogue.Publish(new PingNotificationN2("n2"));

    [Benchmark]
    public Task MediatR_Publish_N2() => _mediatR.Publish(new MtrPingNotificationN2("n2"));

    [Benchmark]
    public ValueTask Mediator_Publish_N2() => _mediator.Publish(new MedPingNotificationN2("n2"));

    // ── Scenario 4b — N = 5 ───────────────────────────────────────────────────────────────
    [Benchmark]
    public ValueTask Rogue_Publish_N5() => _rogue.Publish(new PingNotificationN5("n5"));

    [Benchmark]
    public Task MediatR_Publish_N5() => _mediatR.Publish(new MtrPingNotificationN5("n5"));

    [Benchmark]
    public ValueTask Mediator_Publish_N5() => _mediator.Publish(new MedPingNotificationN5("n5"));

    // ── Scenario 6 (NFR-PERF-5 honesty) — N = 20 ──────────────────────────────────────────
    // Hypothesis A1: Rogue's Publish path resolves handlers via a runtime
    // GetServices<INotificationHandler<PingNotificationN20>>() enumeration on EVERY call, while
    // martinothamar generates a statically-typed fan-out loop. At N=20 this DI lookup is a
    // measurable factor relative to the no-op handler bodies. Record A1's confirmed/refuted/
    // inconclusive status in bench/RESULTS.md after the run.
    [Benchmark]
    public ValueTask Rogue_Publish_N20_Honesty() => _rogue.Publish(new PingNotificationN20("n20")); // NFR-PERF-5

    [Benchmark]
    public Task MediatR_Publish_N20_Honesty() => _mediatR.Publish(new MtrPingNotificationN20("n20")); // NFR-PERF-5

    [Benchmark]
    public ValueTask Mediator_Publish_N20_Honesty() => _mediator.Publish(new MedPingNotificationN20("n20")); // NFR-PERF-5
}
