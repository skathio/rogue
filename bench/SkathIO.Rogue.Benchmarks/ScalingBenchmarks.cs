using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace SkathIO.Rogue.Benchmarks;

// TODO Phase 7.2: 1k-handler scaling — dispatch cost as the registered handler count grows
// (informs PD-3a switch vs FrozenDictionary object-path adequacy).
/// <summary>Stub — handler-count scaling suite delivered in Phase 7.2.</summary>
[MemoryDiagnoser]
public class ScalingBenchmarks
{
    [Benchmark]
    public Task Placeholder() => Task.CompletedTask;
}
