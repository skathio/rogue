using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Benchmarks;

// Simple no-op behavior. RequestHandlerDelegate is parameterless in this codebase
// (the CancellationToken is captured by the generated chain), so next() takes no args.
// Top-level (not nested) so the Rogue generator emits a valid FQN for it.
/// <summary>No-op pipeline behavior used to measure pipeline depth overhead.</summary>
public sealed class NoOpBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public ValueTask<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
        => next();
}

/// <summary>Pipeline depth scaling: Rogue dispatch with N=1,3,5 no-op behaviors.</summary>
[MemoryDiagnoser]
public class NBehaviorsBenchmarks
{
    private ISender _rogue1 = null!;
    private ISender _rogue3 = null!;
    private ISender _rogue5 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rogue1 = BuildRogue(1);
        _rogue3 = BuildRogue(3);
        _rogue5 = BuildRogue(5);
    }

    private static ISender BuildRogue(int n)
    {
        var services = new ServiceCollection();
        services.AddRogue();
        services.AddTransient<IRequestHandler<PingRequest, string>, PingHandler>();
        for (int i = 0; i < n; i++)
        {
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(NoOpBehavior<,>));
        }
        return services.BuildServiceProvider().GetRequiredService<ISender>();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<string> Rogue_1Behavior() => _rogue1.Send(new PingRequest("ping"));

    [Benchmark]
    public ValueTask<string> Rogue_3Behaviors() => _rogue3.Send(new PingRequest("ping"));

    [Benchmark]
    public ValueTask<string> Rogue_5Behaviors() => _rogue5.Send(new PingRequest("ping"));
}
