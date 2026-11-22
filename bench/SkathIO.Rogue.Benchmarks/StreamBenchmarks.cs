using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Benchmarks;

/// <summary>
/// Scenario 5 — <c>CreateStream</c> over a 10-element <see cref="System.Collections.Generic.IAsyncEnumerable{T}"/>,
/// Rogue against MediatR. MediatR 12 streams through <c>IStreamRequest&lt;T&gt;</c> and
/// <c>ISender.CreateStream</c>; both handlers yield the same ten integers, and each benchmark
/// enumerates the whole stream. Rogue is the BenchmarkDotNet baseline.
/// </summary>
[MemoryDiagnoser]
public class StreamBenchmarks
{
    private SkathIO.Rogue.ISender _rogue = null!;
    private global::MediatR.ISender _mediatR = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rogueServices = new ServiceCollection();
        rogueServices.AddRogue();
        rogueServices.AddTransient<IStreamQueryHandler<PingStreamRequest, int>, PingStreamHandler>();
        _rogue = rogueServices.BuildServiceProvider().GetRequiredService<SkathIO.Rogue.ISender>();

        var mediatRServices = new ServiceCollection();
        mediatRServices.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<MtrPingStreamHandler>());
        _mediatR = mediatRServices.BuildServiceProvider().GetRequiredService<global::MediatR.ISender>();
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
    public async Task<int> MediatR_CreateStream_10Items()
    {
        int sum = 0;
        await foreach (var item in _mediatR.CreateStream(new MtrPingStreamRequest(10)))
        {
            sum += item;
        }
        return sum;
    }
}
