using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Benchmarks;

/// <summary>
/// Scenario 3 / 3b — object-path (untyped) dispatch. Rogue's generated <c>SendObject</c> switch
/// (reached via <see cref="ISender.Send(object, CancellationToken)"/>) is benchmarked head-to-head
/// against the equivalent object-dispatch path on MediatR and martinothamar/Mediator (scenario 3),
/// and against itself at 1 vs 25 registered handler types (scenario 3b) to gather PD-3a evidence on
/// whether the generated <c>switch</c> dispatch scales sub-linearly — the question that gates the
/// FrozenDictionary deferral (NFR-PERF-5).
/// </summary>
/// <remarks>
/// Unlike the typed scenario-1 path, Rogue DOES expose a real untyped object path here:
/// <see cref="ISender.Send(object, CancellationToken)"/> forwards to the generated
/// <c>RogueDispatcher.SendObject</c> switch (boxes the response by design). So all three libraries
/// exercise their genuine object-dispatch surface in scenario 3 — no typed-path surrogate needed.
/// </remarks>
[MemoryDiagnoser]
public class ObjectPathBenchmarks
{
    // Scenario 3: object-path dispatch, single handler registered.
    private ISender _rogue = null!;
    private global::MediatR.ISender _mediatR = null!;
    private global::Mediator.IMediator _mediator = null!;

    // Scenario 3b: object-path dispatch with 25 distinct handler types registered (PD-3a evidence).
    private ISender _rogueScaling = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Scenario 3 — single handler, object dispatch.
        var rogueServices = new ServiceCollection();
        rogueServices.AddRogue(o => o.EnableObjectDispatch = true);
        rogueServices.AddTransient<IRequestHandler<PingRequest, string>, PingHandler>();
        _rogue = rogueServices.BuildServiceProvider().GetRequiredService<ISender>();

        var mediatRServices = new ServiceCollection();
        mediatRServices.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<MediatRPingHandler>());
        _mediatR = mediatRServices.BuildServiceProvider().GetRequiredService<global::MediatR.ISender>();

        var mediatorServices = new ServiceCollection();
        mediatorServices.AddMediator();
        _mediator = mediatorServices.BuildServiceProvider().GetRequiredService<global::Mediator.IMediator>();

        // Scenario 3b — 25 distinct handler types registered (PingHandler = #1, Scale01..Scale24 = #2..#25).
        var scalingServices = new ServiceCollection();
        scalingServices.AddRogue(o => o.EnableObjectDispatch = true);
        scalingServices.AddTransient<IRequestHandler<PingRequest, string>, PingHandler>();
        scalingServices.AddTransient<IRequestHandler<Scale01Request, int>, Scale01Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale02Request, int>, Scale02Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale03Request, int>, Scale03Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale04Request, int>, Scale04Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale05Request, int>, Scale05Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale06Request, int>, Scale06Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale07Request, int>, Scale07Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale08Request, int>, Scale08Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale09Request, int>, Scale09Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale10Request, int>, Scale10Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale11Request, int>, Scale11Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale12Request, int>, Scale12Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale13Request, int>, Scale13Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale14Request, int>, Scale14Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale15Request, int>, Scale15Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale16Request, int>, Scale16Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale17Request, int>, Scale17Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale18Request, int>, Scale18Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale19Request, int>, Scale19Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale20Request, int>, Scale20Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale21Request, int>, Scale21Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale22Request, int>, Scale22Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale23Request, int>, Scale23Handler>();
        scalingServices.AddTransient<IRequestHandler<Scale24Request, int>, Scale24Handler>();
        _rogueScaling = scalingServices.BuildServiceProvider().GetRequiredService<ISender>();
    }

    // Scenario 3 — object-path dispatch, 1 handler. Rogue baseline.
    [Benchmark(Baseline = true)]
    public ValueTask<object?> Rogue_SendObject_1Handler()
        => _rogue.Send((object)new PingRequest("obj"));

    [Benchmark]
    public Task<object?> MediatR_SendObject()
        => _mediatR.Send((object)new MediatRPingRequest("obj"));

    [Benchmark]
    public ValueTask<object?> Mediator_SendObject()
        => _mediator.Send((object)new MedPingRequest("obj"));

    // Scenario 3b — object-path dispatch, 25 registered handler types (PD-3a / NFR-PERF-5).
    // The measurement question: does Rogue's generated switch scale O(1) or O(N) at 25 types?
    // If Rogue_SendObject_25Handlers ≈ Rogue_SendObject_1Handler, the switch is adequate and the
    // FrozenDictionary optimization stays deferred (PD-3a). A clear regression argues for it.
    [Benchmark]
    public ValueTask<object?> Rogue_SendObject_25Handlers()
        => _rogueScaling.Send((object)new PingRequest("scale")); // NFR-PERF-5
}

// ── 24 parallel request/handler pairs for scenario 3b scaling measurement (PD-3a) ────────────────
// Distinct types are required: the generator discovers each one and emits a separate switch arm, so
// the 25-arm dispatch switch only exists if 25 distinct request types are declared. PingRequest (in
// Shared/BenchmarkHandlers.cs) is handler #1; Scale01..Scale24 below are handlers #2..#25.

/// <summary>Scaling request #2 (PD-3a 25-handler switch).</summary>
public sealed record Scale01Request(int P) : IRequest<int>;
/// <summary>Scaling handler #2.</summary>
public sealed class Scale01Handler : IRequestHandler<Scale01Request, int>
{ public ValueTask<int> Handle(Scale01Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #3 (PD-3a 25-handler switch).</summary>
public sealed record Scale02Request(int P) : IRequest<int>;
/// <summary>Scaling handler #3.</summary>
public sealed class Scale02Handler : IRequestHandler<Scale02Request, int>
{ public ValueTask<int> Handle(Scale02Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #4 (PD-3a 25-handler switch).</summary>
public sealed record Scale03Request(int P) : IRequest<int>;
/// <summary>Scaling handler #4.</summary>
public sealed class Scale03Handler : IRequestHandler<Scale03Request, int>
{ public ValueTask<int> Handle(Scale03Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #5 (PD-3a 25-handler switch).</summary>
public sealed record Scale04Request(int P) : IRequest<int>;
/// <summary>Scaling handler #5.</summary>
public sealed class Scale04Handler : IRequestHandler<Scale04Request, int>
{ public ValueTask<int> Handle(Scale04Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #6 (PD-3a 25-handler switch).</summary>
public sealed record Scale05Request(int P) : IRequest<int>;
/// <summary>Scaling handler #6.</summary>
public sealed class Scale05Handler : IRequestHandler<Scale05Request, int>
{ public ValueTask<int> Handle(Scale05Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #7 (PD-3a 25-handler switch).</summary>
public sealed record Scale06Request(int P) : IRequest<int>;
/// <summary>Scaling handler #7.</summary>
public sealed class Scale06Handler : IRequestHandler<Scale06Request, int>
{ public ValueTask<int> Handle(Scale06Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #8 (PD-3a 25-handler switch).</summary>
public sealed record Scale07Request(int P) : IRequest<int>;
/// <summary>Scaling handler #8.</summary>
public sealed class Scale07Handler : IRequestHandler<Scale07Request, int>
{ public ValueTask<int> Handle(Scale07Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #9 (PD-3a 25-handler switch).</summary>
public sealed record Scale08Request(int P) : IRequest<int>;
/// <summary>Scaling handler #9.</summary>
public sealed class Scale08Handler : IRequestHandler<Scale08Request, int>
{ public ValueTask<int> Handle(Scale08Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #10 (PD-3a 25-handler switch).</summary>
public sealed record Scale09Request(int P) : IRequest<int>;
/// <summary>Scaling handler #10.</summary>
public sealed class Scale09Handler : IRequestHandler<Scale09Request, int>
{ public ValueTask<int> Handle(Scale09Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #11 (PD-3a 25-handler switch).</summary>
public sealed record Scale10Request(int P) : IRequest<int>;
/// <summary>Scaling handler #11.</summary>
public sealed class Scale10Handler : IRequestHandler<Scale10Request, int>
{ public ValueTask<int> Handle(Scale10Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #12 (PD-3a 25-handler switch).</summary>
public sealed record Scale11Request(int P) : IRequest<int>;
/// <summary>Scaling handler #12.</summary>
public sealed class Scale11Handler : IRequestHandler<Scale11Request, int>
{ public ValueTask<int> Handle(Scale11Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #13 (PD-3a 25-handler switch).</summary>
public sealed record Scale12Request(int P) : IRequest<int>;
/// <summary>Scaling handler #13.</summary>
public sealed class Scale12Handler : IRequestHandler<Scale12Request, int>
{ public ValueTask<int> Handle(Scale12Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #14 (PD-3a 25-handler switch).</summary>
public sealed record Scale13Request(int P) : IRequest<int>;
/// <summary>Scaling handler #14.</summary>
public sealed class Scale13Handler : IRequestHandler<Scale13Request, int>
{ public ValueTask<int> Handle(Scale13Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #15 (PD-3a 25-handler switch).</summary>
public sealed record Scale14Request(int P) : IRequest<int>;
/// <summary>Scaling handler #15.</summary>
public sealed class Scale14Handler : IRequestHandler<Scale14Request, int>
{ public ValueTask<int> Handle(Scale14Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #16 (PD-3a 25-handler switch).</summary>
public sealed record Scale15Request(int P) : IRequest<int>;
/// <summary>Scaling handler #16.</summary>
public sealed class Scale15Handler : IRequestHandler<Scale15Request, int>
{ public ValueTask<int> Handle(Scale15Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #17 (PD-3a 25-handler switch).</summary>
public sealed record Scale16Request(int P) : IRequest<int>;
/// <summary>Scaling handler #17.</summary>
public sealed class Scale16Handler : IRequestHandler<Scale16Request, int>
{ public ValueTask<int> Handle(Scale16Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #18 (PD-3a 25-handler switch).</summary>
public sealed record Scale17Request(int P) : IRequest<int>;
/// <summary>Scaling handler #18.</summary>
public sealed class Scale17Handler : IRequestHandler<Scale17Request, int>
{ public ValueTask<int> Handle(Scale17Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #19 (PD-3a 25-handler switch).</summary>
public sealed record Scale18Request(int P) : IRequest<int>;
/// <summary>Scaling handler #19.</summary>
public sealed class Scale18Handler : IRequestHandler<Scale18Request, int>
{ public ValueTask<int> Handle(Scale18Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #20 (PD-3a 25-handler switch).</summary>
public sealed record Scale19Request(int P) : IRequest<int>;
/// <summary>Scaling handler #20.</summary>
public sealed class Scale19Handler : IRequestHandler<Scale19Request, int>
{ public ValueTask<int> Handle(Scale19Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #21 (PD-3a 25-handler switch).</summary>
public sealed record Scale20Request(int P) : IRequest<int>;
/// <summary>Scaling handler #21.</summary>
public sealed class Scale20Handler : IRequestHandler<Scale20Request, int>
{ public ValueTask<int> Handle(Scale20Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #22 (PD-3a 25-handler switch).</summary>
public sealed record Scale21Request(int P) : IRequest<int>;
/// <summary>Scaling handler #22.</summary>
public sealed class Scale21Handler : IRequestHandler<Scale21Request, int>
{ public ValueTask<int> Handle(Scale21Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #23 (PD-3a 25-handler switch).</summary>
public sealed record Scale22Request(int P) : IRequest<int>;
/// <summary>Scaling handler #23.</summary>
public sealed class Scale22Handler : IRequestHandler<Scale22Request, int>
{ public ValueTask<int> Handle(Scale22Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #24 (PD-3a 25-handler switch).</summary>
public sealed record Scale23Request(int P) : IRequest<int>;
/// <summary>Scaling handler #24.</summary>
public sealed class Scale23Handler : IRequestHandler<Scale23Request, int>
{ public ValueTask<int> Handle(Scale23Request r, CancellationToken ct) => new(r.P); }

/// <summary>Scaling request #25 (PD-3a 25-handler switch).</summary>
public sealed record Scale24Request(int P) : IRequest<int>;
/// <summary>Scaling handler #25.</summary>
public sealed class Scale24Handler : IRequestHandler<Scale24Request, int>
{ public ValueTask<int> Handle(Scale24Request r, CancellationToken ct) => new(r.P); }
