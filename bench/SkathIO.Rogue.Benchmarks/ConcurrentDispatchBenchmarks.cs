using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using SkathIO.Rogue;

namespace SkathIO.Rogue.Benchmarks;

/// <summary>
/// Concurrent-dispatch scenario (Phase 9.4 / AC-G) — fan <c>Concurrency</c> independent
/// <see cref="ISender.Send{TResponse}"/> calls out across distinct DI scopes via
/// <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/> and await them together.
/// This is a Rogue-only scenario: it stresses the per-scope dispatcher resolution + concurrent
/// dispatch path the head-to-head comparison classes (single-threaded) do not exercise. The
/// <see cref="ThreadingDiagnoser"/> + <see cref="MemoryDiagnoser"/> attributes report lock contention,
/// completed work items, and per-operation allocation.
/// </summary>
/// <remarks>
/// The generated dispatcher is registered <c>Scoped</c> (7.3.1 captive-dependency fix), so resolving
/// <see cref="ISender"/> from an independent <see cref="IServiceScope"/> per call mirrors how a real
/// request-scoped consumer dispatches under concurrency. The handler is <c>Transient</c>.
/// </remarks>
[ThreadingDiagnoser]
[MemoryDiagnoser]
public class ConcurrentDispatchBenchmarks
{
    private IServiceProvider _provider = null!;

    [Params(1, 4, 8, 16)]
    public int Concurrency { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddRogue();
        services.AddTransient<ICommandHandler<PingRequest, string>, PingHandler>();
        _provider = services.BuildServiceProvider();
    }

    [Benchmark]
    public async Task ConcurrentSend()
    {
        var tasks = new Task<string>[Concurrency];
        var scopes = new IServiceScope[Concurrency];
        for (var i = 0; i < Concurrency; i++)
        {
            var scope = _provider.CreateScope();
            scopes[i] = scope;
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            tasks[i] = sender.Send(new PingRequest("ping")).AsTask();
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        for (var i = 0; i < Concurrency; i++)
        {
            scopes[i].Dispose();
        }
    }
}
