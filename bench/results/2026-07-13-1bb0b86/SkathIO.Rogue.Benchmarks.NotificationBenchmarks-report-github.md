```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                      | Mean       | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------- |-----------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_Publish_N2            |   129.9 ns |  31.71 ns |  1.74 ns |  1.00 |    0.02 | 0.0355 |     112 B |        1.00 |
| MediatR_Publish_N2          |   218.9 ns | 118.72 ns |  6.51 ns |  1.69 |    0.05 | 0.1478 |     464 B |        4.14 |
| Rogue_Publish_N5            |   257.1 ns | 160.39 ns |  8.79 ns |  1.98 |    0.06 | 0.0663 |     208 B |        1.86 |
| MediatR_Publish_N5          |   404.8 ns | 257.01 ns | 14.09 ns |  3.12 |    0.10 | 0.2933 |     920 B |        8.21 |
| Rogue_Publish_N20_Honesty   | 1,054.5 ns | 275.81 ns | 15.12 ns |  8.12 |    0.14 | 0.2193 |     688 B |        6.14 |
| MediatR_Publish_N20_Honesty | 1,389.2 ns | 489.11 ns | 26.81 ns | 10.70 |    0.22 | 1.0185 |    3200 B |       28.57 |
