```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.108
  [Host]   : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                       | Mean        | Error        | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------- |------------:|-------------:|----------:|------:|--------:|-------:|----------:|------------:|
| Rogue_Publish_N2             |   353.54 ns |    97.304 ns |  5.334 ns |  1.00 |    0.02 | 0.2499 |     784 B |        1.00 |
| MediatR_Publish_N2           |   211.76 ns |    56.098 ns |  3.075 ns |  0.60 |    0.01 | 0.1478 |     464 B |        0.59 |
| Mediator_Publish_N2          |    22.95 ns |     7.710 ns |  0.423 ns |  0.06 |    0.00 | 0.0076 |      24 B |        0.03 |
| Rogue_Publish_N5             |   727.37 ns |   597.169 ns | 32.733 ns |  2.06 |    0.08 | 0.6170 |    1936 B |        2.47 |
| MediatR_Publish_N5           |   390.24 ns |   172.961 ns |  9.481 ns |  1.10 |    0.03 | 0.2933 |     920 B |        1.17 |
| Mediator_Publish_N5          |    42.98 ns |    28.616 ns |  1.569 ns |  0.12 |    0.00 | 0.0076 |      24 B |        0.03 |
| Rogue_Publish_N20_Honesty    | 2,445.49 ns | 1,081.625 ns | 59.288 ns |  6.92 |    0.17 | 2.3308 |    7312 B |        9.33 |
| MediatR_Publish_N20_Honesty  | 1,363.38 ns |   606.944 ns | 33.269 ns |  3.86 |    0.10 | 1.0185 |    3200 B |        4.08 |
| Mediator_Publish_N20_Honesty |   141.27 ns |    64.928 ns |  3.559 ns |  0.40 |    0.01 | 0.0076 |      24 B |        0.03 |
