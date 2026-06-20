```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                          | Mean     | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------------- |---------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_1Behavior                 | 379.1 ns | 121.51 ns |  6.66 ns |  1.00 |    0.02 | 0.2780 |     872 B |        1.00 |
| Rogue_3Behaviors                | 379.8 ns | 179.36 ns |  9.83 ns |  1.00 |    0.03 | 0.2780 |     872 B |        1.00 |
| Rogue_5Behaviors                | 364.9 ns |  55.15 ns |  3.02 ns |  0.96 |    0.02 | 0.2780 |     872 B |        1.00 |
| Rogue_1Behavior_Chain_Concrete  | 366.5 ns | 192.81 ns | 10.57 ns |  0.97 |    0.03 | 0.2780 |     872 B |        1.00 |
| Rogue_3Behaviors_Chain_Concrete | 338.0 ns |  42.83 ns |  2.35 ns |  0.89 |    0.01 | 0.2780 |     872 B |        1.00 |
| Rogue_5Behaviors_Chain_Concrete | 349.4 ns | 235.62 ns | 12.92 ns |  0.92 |    0.03 | 0.2780 |     872 B |        1.00 |
