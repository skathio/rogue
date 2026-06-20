using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;
using SkathIO.Rogue.Generated; // RogueExtensions — the generated concrete-dispatch fast path (D3/D5)

namespace SkathIO.Rogue.Benchmarks;

// Dedicated request type for the D5 chain benchmarks. It is DISTINCT from the shared PingRequest
// (Shared/BenchmarkHandlers.cs) on purpose: the source generator's behavior-list factory and chain
// emission are keyed per request TYPE, compilation-wide. If the closed PingClosedNoOpBehavior types
// below applied to PingRequest, the generator would register an IReadOnlyList<IPipelineBehavior<
// PingRequest,string>> factory that eagerly resolves all five behaviors on every PingRequest dispatch —
// contaminating NoBehaviorBenchmarks / ObjectPathBenchmarks / ColdStartBenchmarks (which share
// PingRequest and expect the zero-behavior fast path). Attaching the behaviors to ChainPingRequest
// instead keeps PingRequest behavior-free for those headline scenarios while still exercising the real
// D5 chain here. (See the rogue-perf Phase 5 diary plan-change entry.)
#pragma warning disable SA1402 // multiple types per file: the chain request, handler, and five behaviors are one benchmark fixture
public sealed record ChainPingRequest(string Payload) : ICommand<string>;

public sealed class ChainPingHandler : ICommandHandler<ChainPingRequest, string>
{
    public ValueTask<string> Handle(ChainPingRequest request, CancellationToken ct) => new(request.Payload);
}

// Closed no-op behaviors for the D5 chain benchmarks — each applies ONLY to ChainPingRequest. A closed
// behavior is what activates the source generator's statically-typed chain emission
// (Send_ChainPingRequest_Chain_N) instead of the runtime PipelineExecutor fold an open
// IPipelineBehavior<,> would force. This file deliberately declares NO open generic behavior: a usable
// open non-stream IPipelineBehavior<,> is type-scanned by the generator and vetoes the D5 chain for EVERY
// non-stream request in the compilation (HasUsableOpenBehavior, compilation-wide), which would silently
// push ChainPingRequest's _WithBehaviors back onto PipelineExecutor.Execute. With closed-only behaviors
// here, BOTH benchmark families below hit the emitted D5 chain.
//
// FIVE DISTINCT types (not one type registered N times): the generator's IReadOnlyList factory is keyed
// on the set of distinct closed behavior FQNs it discovers applicable to ChainPingRequest, so registering
// one type N times still yields a length-1 list (Chain_1) — only N distinct types make _WithBehaviors
// route into Chain_N. They are independent concrete classes (no shared abstract base — an abstract closed
// behavior trips ROGUE005), each a trivial pass-through.
public sealed class PingClosedNoOpBehavior1 : IPipelineBehavior<ChainPingRequest, string>
{
    public ValueTask<string> Handle(ChainPingRequest request, RequestHandlerDelegate<string> next, CancellationToken ct) => next();
}
public sealed class PingClosedNoOpBehavior2 : IPipelineBehavior<ChainPingRequest, string>
{
    public ValueTask<string> Handle(ChainPingRequest request, RequestHandlerDelegate<string> next, CancellationToken ct) => next();
}
public sealed class PingClosedNoOpBehavior3 : IPipelineBehavior<ChainPingRequest, string>
{
    public ValueTask<string> Handle(ChainPingRequest request, RequestHandlerDelegate<string> next, CancellationToken ct) => next();
}
public sealed class PingClosedNoOpBehavior4 : IPipelineBehavior<ChainPingRequest, string>
{
    public ValueTask<string> Handle(ChainPingRequest request, RequestHandlerDelegate<string> next, CancellationToken ct) => next();
}
public sealed class PingClosedNoOpBehavior5 : IPipelineBehavior<ChainPingRequest, string>
{
    public ValueTask<string> Handle(ChainPingRequest request, RequestHandlerDelegate<string> next, CancellationToken ct) => next();
}
#pragma warning restore SA1402

/// <summary>Pipeline depth scaling: Rogue dispatch with N=1,3,5 no-op behaviors. Rogue-only (no MediatR
/// comparison) — this is an internal scaling/allocation check, not a headline "vs MediatR" scenario.</summary>
/// <remarks>
/// Two families of benchmarks over the dedicated <c>ChainPingRequest</c>. BOTH exercise the emitted D5
/// statically-typed chain (this file declares only CLOSED, per-<c>ChainPingRequest</c> behaviors — no open
/// generic that would veto chain emission compilation-wide). The distinction between the families is the
/// dispatch ENTRY POINT, not chain vs fold:
/// <list type="bullet">
/// <item><c>Rogue_*Behavior(s)</c> — N DISTINCT CLOSED behaviors (<c>PingClosedNoOpBehavior1..N</c>) via
/// <c>ISender.Send</c>. Measures the <c>ISender</c> interface-dispatch overhead (one boxed
/// <c>ValueTask&lt;T&gt;</c>) PLUS the D5 chain.</item>
/// <item><c>Rogue_*Behaviors_Chain_Concrete</c> — the SAME N distinct closed behaviors via the generated
/// concrete <c>RogueDispatcher.SendChainPingRequest</c> fast path (D3). Measures the D5 chain via the
/// near-zero-alloc concrete entry point WITHOUT the <c>ISender</c> box.</item>
/// </list>
/// N distinct closed types make the generator's IReadOnlyList factory yield a length-N list, so
/// <c>_WithBehaviors</c> routes into <c>Chain_N</c> (Chain_1/3/5 for N=1/3/5) — registering one type N
/// times would instead always hit <c>Chain_1</c>. The D5 chain eliminates <c>PipelineState</c> struct
/// boxing; the innermost <c>() =&gt; handler.Handle(...)</c> and each per-link <c>() =&gt;</c> forwarding
/// lambda still allocate a delegate, so a literal "0 B" on the chain is aspirational — Phase 5 records the
/// measured bytes (see the plan diary).
/// </remarks>
[MemoryDiagnoser]
public class NBehaviorsBenchmarks
{
    private ISender _rogue1 = null!;
    private ISender _rogue3 = null!;
    private ISender _rogue5 = null!;

    // D5 concrete-chain path (closed behavior). The RogueDispatcher is registered Scoped, so each held
    // scope keeps its resolved dispatcher alive for the whole benchmark run.
    private global::SkathIO.Rogue.RogueDispatcher _rogueChain1 = null!;
    private global::SkathIO.Rogue.RogueDispatcher _rogueChain3 = null!;
    private global::SkathIO.Rogue.RogueDispatcher _rogueChain5 = null!;
    private IServiceScope _chainScope1 = null!;
    private IServiceScope _chainScope3 = null!;
    private IServiceScope _chainScope5 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rogue1 = BuildRogue(1);
        _rogue3 = BuildRogue(3);
        _rogue5 = BuildRogue(5);

        _rogueChain1 = BuildRogueConcreteChain(1, out _chainScope1);
        _rogueChain3 = BuildRogueConcreteChain(3, out _chainScope3);
        _rogueChain5 = BuildRogueConcreteChain(5, out _chainScope5);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _chainScope1?.Dispose();
        _chainScope3?.Dispose();
        _chainScope5?.Dispose();
    }

    // The five DISTINCT closed behavior types, in chain order. Both BuildRogue (ISender path) and
    // BuildRogueConcreteChain (concrete path) register the first N — so the generated _WithBehaviors
    // switch sees behaviors.Count == N at runtime and routes into the matching
    // Send_ChainPingRequest_Chain_N (not Chain_1 N times). The generator discovers all five applicable to
    // ChainPingRequest and emits Chain_1..5 (within MAX_STATIC_CHAIN_DEPTH = 8).
    private static readonly System.Type[] ClosedBehaviorTypes =
    {
        typeof(PingClosedNoOpBehavior1), typeof(PingClosedNoOpBehavior2), typeof(PingClosedNoOpBehavior3),
        typeof(PingClosedNoOpBehavior4), typeof(PingClosedNoOpBehavior5),
    };

    // ISender path: register the first N DISTINCT closed behaviors. With no open generic in this
    // compilation, _WithBehaviors routes into the emitted D5 Send_ChainPingRequest_Chain_N — so this
    // family measures ISender interface-dispatch overhead + the chain (NOT the PipelineExecutor fold).
    private static ISender BuildRogue(int n)
    {
        var services = new ServiceCollection();
        services.AddRogue();
        services.AddTransient<ICommandHandler<ChainPingRequest, string>, ChainPingHandler>();
        for (int i = 0; i < n; i++)
        {
            services.AddTransient(typeof(IPipelineBehavior<ChainPingRequest, string>), ClosedBehaviorTypes[i]);
        }
        return services.BuildServiceProvider().GetRequiredService<ISender>();
    }

    private static global::SkathIO.Rogue.RogueDispatcher BuildRogueConcreteChain(int n, out IServiceScope scope)
    {
        var services = new ServiceCollection();
        services.AddRogue();
        services.AddTransient<ICommandHandler<ChainPingRequest, string>, ChainPingHandler>();
        for (int i = 0; i < n; i++)
        {
            services.AddTransient(typeof(IPipelineBehavior<ChainPingRequest, string>), ClosedBehaviorTypes[i]);
        }
        var sp = services.BuildServiceProvider();
        scope = sp.CreateScope();
        return scope.ServiceProvider.GetRequiredService<global::SkathIO.Rogue.RogueDispatcher>();
    }

    // ── D5 chain via the ISender interface path (closed behaviors): chain + one ValueTask<T> box ──
    [Benchmark(Baseline = true)]
    public ValueTask<string> Rogue_1Behavior() => _rogue1.Send(new ChainPingRequest("ping"));

    [Benchmark]
    public ValueTask<string> Rogue_3Behaviors() => _rogue3.Send(new ChainPingRequest("ping"));

    [Benchmark]
    public ValueTask<string> Rogue_5Behaviors() => _rogue5.Send(new ChainPingRequest("ping"));

    // ── D5 chain via the concrete dispatch path (closed behaviors): chain, no ISender box ──────

    [Benchmark]
    public ValueTask<string> Rogue_1Behavior_Chain_Concrete()
        => _rogueChain1.SendChainPingRequest(new ChainPingRequest("ping"));

    [Benchmark]
    public ValueTask<string> Rogue_3Behaviors_Chain_Concrete()
        => _rogueChain3.SendChainPingRequest(new ChainPingRequest("ping"));

    [Benchmark]
    public ValueTask<string> Rogue_5Behaviors_Chain_Concrete()
        => _rogueChain5.SendChainPingRequest(new ChainPingRequest("ping"));
}
