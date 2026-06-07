using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Benchmarks;

/// <summary>
/// Scenario 5 — <c>CreateStream</c> over a 10-element <see cref="System.Collections.Generic.IAsyncEnumerable{T}"/>
/// (PD-27). Compares Rogue and martinothamar/Mediator. MediatR v12's <em>core</em> package has no
/// <c>IStreamRequest</c> equivalent (streaming lives in the separate
/// <c>MediatR.Extensions.Microsoft.DependencyInjection</c> surface, not referenced here), so MediatR
/// is intentionally absent from this scenario. Exercises Phase 4.2.1 stream weaving for Rogue.
/// </summary>
[MemoryDiagnoser]
public class StreamBenchmarks
{
    private ISender _rogue = null!;
    private global::Mediator.IMediator _mediator = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rogueServices = new ServiceCollection();
        rogueServices.AddRogue();
        rogueServices.AddTransient<IStreamRequestHandler<PingStreamRequest, int>, PingStreamHandler>();
        _rogue = rogueServices.BuildServiceProvider().GetRequiredService<ISender>();

        // martinothamar: AddMediator() (generated) discovers MedPingStreamHandler and wires CreateStream.
        var mediatorServices = new ServiceCollection();
        mediatorServices.AddMediator();
        _mediator = mediatorServices.BuildServiceProvider().GetRequiredService<global::Mediator.IMediator>();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> Rogue_CreateStream_10Items()
    {
        int sum = 0;
        await foreach (var item in _rogue.CreateStream(new PingStreamRequest(10)))
        {
            sum += item;
        }
        return sum;
    }

    [Benchmark]
    public async Task<int> Mediator_CreateStream_10Items()
    {
        int sum = 0;
        await foreach (var item in _mediator.CreateStream(new MedPingStreamRequest(10)))
        {
            sum += item;
        }
        return sum;
    }
}
