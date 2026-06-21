using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Benchmarks;

// NFR-PERF-5 honesty note: cold-start is expected to be similar or slower than MediatR.
// The DI container build + module-init overhead is not Rogue's differentiator — Rogue wins
// on the hot dispatch path (NFR-PERF-1/2), not on container construction. Measuring this
// scenario keeps the published suite honest (a suite where the author wins everything is not
// credible — NFR-PERF-5).
/// <summary>Cold-start: build a fresh <c>ServiceProvider</c> and dispatch once.</summary>
[MemoryDiagnoser]
public class ColdStartBenchmarks
{
    [Benchmark(Baseline = true)]
    public async Task<string> Rogue_ColdStart()
    {
        var services = new ServiceCollection();
        services.AddRogue();
        services.AddTransient<ICommandHandler<PingRequest, string>, PingHandler>();
        var sp = services.BuildServiceProvider();
        return await sp.GetRequiredService<ISender>().Send(new PingRequest("ping"));
    }

    [Benchmark]
    public async Task<string> MediatR_ColdStart()
    {
        var services = new ServiceCollection();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<MediatRPingHandler>());
        var sp = services.BuildServiceProvider();
        return await sp.GetRequiredService<global::MediatR.ISender>().Send(new MediatRPingRequest("ping"));
    }
}
