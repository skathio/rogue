```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                          | Mean     | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------------- |---------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_1Behavior                 | 374.9 ns | 189.54 ns | 10.39 ns |  1.00 |    0.03 | 0.2780 |     872 B |        1.00 |
| Rogue_3Behaviors                | 362.0 ns |  80.35 ns |  4.40 ns |  0.97 |    0.03 | 0.2780 |     872 B |        1.00 |
| Rogue_5Behaviors                | 385.8 ns | 284.53 ns | 15.60 ns |  1.03 |    0.04 | 0.2780 |     872 B |        1.00 |
| Rogue_1Behavior_Chain_Concrete  | 353.5 ns | 107.77 ns |  5.91 ns |  0.94 |    0.03 | 0.2780 |     872 B |        1.00 |
| Rogue_3Behaviors_Chain_Concrete | 357.8 ns | 213.94 ns | 11.73 ns |  0.95 |    0.04 | 0.2780 |     872 B |        1.00 |
| Rogue_5Behaviors_Chain_Concrete | 345.7 ns |  53.21 ns |  2.92 ns |  0.92 |    0.02 | 0.2780 |     872 B |        1.00 |
