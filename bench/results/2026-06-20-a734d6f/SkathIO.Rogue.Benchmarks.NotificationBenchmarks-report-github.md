```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                      | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------- |-----------:|------------:|----------:|------:|--------:|-------:|----------:|------------:|
| Rogue_Publish_N2            |   120.6 ns |    57.82 ns |   3.17 ns |  1.00 |    0.03 | 0.0355 |     112 B |        1.00 |
| MediatR_Publish_N2          |   209.9 ns |   157.74 ns |   8.65 ns |  1.74 |    0.07 | 0.1478 |     464 B |        4.14 |
| Rogue_Publish_N5            |   258.0 ns |   223.74 ns |  12.26 ns |  2.14 |    0.10 | 0.0663 |     208 B |        1.86 |
| MediatR_Publish_N5          |   409.6 ns |   221.03 ns |  12.12 ns |  3.40 |    0.12 | 0.2933 |     920 B |        8.21 |
| Rogue_Publish_N20_Honesty   |   883.6 ns |   136.19 ns |   7.46 ns |  7.33 |    0.18 | 0.2193 |     688 B |        6.14 |
| MediatR_Publish_N20_Honesty | 1,459.2 ns | 2,144.33 ns | 117.54 ns | 12.11 |    0.89 | 1.0185 |    3200 B |       28.57 |
