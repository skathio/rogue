using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;
using SkathIO.Rogue.Generated; // RogueExtensions — the generated concrete-dispatch fast path (D3)

namespace SkathIO.Rogue.Benchmarks;

/// <summary>
/// Scenario 1 — core hot-path dispatch, zero behaviors, sync-completing handler. Compares Rogue
/// (both the ISender interface path and the generated concrete fast path) against MediatR as sibling
/// methods in one class so BenchmarkDotNet emits a ranked ratio report, Rogue ISender baseline.
/// </summary>
/// <remarks>
/// PD-12 / AC-6.2 / rogue-perf D3: the genuinely 0-alloc Rogue path is the generated concrete method
/// reached via <c>RogueDispatcher.SendPingRequest()</c> (the public <c>RogueExtensions</c> extension
/// that downcasts to the <c>internal</c> impl and calls <c>Send_PingRequest</c> directly), NOT the
/// <c>ISender.Send&lt;T&gt;()</c> interface path (which boxes one <c>ValueTask&lt;T&gt;</c> by design).
/// <see cref="Rogue_NoBehavior_Concrete"/> exercises the concrete path and is the AC-6 0-byte gate;
/// <see cref="Rogue_NoBehavior"/> keeps measuring the ISender path (expected one box).
/// The concrete path requires injecting <c>RogueDispatcher</c> (the public base, scoped in DI), not
/// <c>ISender</c> — the latter resolves to <c>Mediator</c>, which the downcast would reject.
/// </remarks>
[MemoryDiagnoser]
public class NoBehaviorBenchmarks
{
    private ISender _rogue = null!;
    private global::SkathIO.Rogue.RogueDispatcher _rogueConcrete = null!;
    private IServiceScope _rogueScope = null!; // held for the benchmark lifetime so the scoped RogueDispatcher stays alive
    private global::MediatR.ISender _mediatR = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rogueServices = new ServiceCollection();
        rogueServices.AddRogue();
        rogueServices.AddTransient<ICommandHandler<PingRequest, string>, PingHandler>();
        var rogueProvider = rogueServices.BuildServiceProvider();
        _rogue = rogueProvider.GetRequiredService<ISender>();

        // RogueDispatcher is registered Scoped — create one scope and hold it (via _rogueScope) for the
        // whole benchmark run so the resolved dispatcher is not collected between iterations.
        _rogueScope = rogueProvider.CreateScope();
        _rogueConcrete = _rogueScope.ServiceProvider.GetRequiredService<global::SkathIO.Rogue.RogueDispatcher>();

        var mediatRServices = new ServiceCollection();
        mediatRServices.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<MediatRPingHandler>());
        _mediatR = mediatRServices.BuildServiceProvider().GetRequiredService<global::MediatR.ISender>();
    }

    [GlobalCleanup]
    public void Cleanup() => _rogueScope?.Dispose();

    [Benchmark(Baseline = true)]
    public ValueTask<string> Rogue_NoBehavior()
        => _rogue.Send(new PingRequest("ping"));

    /// <summary>
    /// AC-6: the generated concrete fast path. Calls the public <c>SendPingRequest</c> extension, which
    /// downcasts <c>RogueDispatcher</c> to the impl and dispatches directly — no <c>ISender</c> box.
    /// Expected: 0 B allocated.
    /// </summary>
    [Benchmark]
    public ValueTask<string> Rogue_NoBehavior_Concrete()
        => _rogueConcrete.SendPingRequest(new PingRequest("ping"));

    [Benchmark]
    public Task<string> MediatR_NoBehavior()
        => _mediatR.Send(new MediatRPingRequest("ping"));
}
