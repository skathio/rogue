```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.115
  [Host]     : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3


```
| Method                          | Mean     | Error   | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------------- |---------:|--------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_1Behavior                 | 350.4 ns | 4.69 ns |  3.92 ns |  1.00 |    0.02 | 0.2780 |     872 B |        1.00 |
| Rogue_3Behaviors                | 355.8 ns | 3.97 ns |  3.32 ns |  1.02 |    0.01 | 0.2780 |     872 B |        1.00 |
| Rogue_5Behaviors                | 352.4 ns | 4.71 ns |  3.94 ns |  1.01 |    0.02 | 0.2780 |     872 B |        1.00 |
| Rogue_1Behavior_Chain_Concrete  | 325.6 ns | 5.17 ns |  9.06 ns |  0.93 |    0.03 | 0.2780 |     872 B |        1.00 |
| Rogue_3Behaviors_Chain_Concrete | 318.9 ns | 6.46 ns | 11.81 ns |  0.91 |    0.03 | 0.2780 |     872 B |        1.00 |
| Rogue_5Behaviors_Chain_Concrete | 320.1 ns | 1.28 ns |  1.07 ns |  0.91 |    0.01 | 0.2780 |     872 B |        1.00 |
