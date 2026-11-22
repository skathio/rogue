```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.115
  [Host]     : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3


```
| Method                      | Mean       | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------- |-----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_Publish_N2            |   113.0 ns |  1.49 ns |  1.89 ns |  1.00 |    0.02 | 0.0356 |     112 B |        1.00 |
| MediatR_Publish_N2          |   195.8 ns |  3.82 ns |  4.55 ns |  1.73 |    0.05 | 0.1478 |     464 B |        4.14 |
| Rogue_Publish_N5            |   238.9 ns |  3.36 ns |  2.63 ns |  2.11 |    0.04 | 0.0663 |     208 B |        1.86 |
| MediatR_Publish_N5          |   364.4 ns |  6.41 ns |  7.63 ns |  3.23 |    0.08 | 0.2933 |     920 B |        8.21 |
| Rogue_Publish_N20_Honesty   |   861.3 ns | 16.01 ns | 24.45 ns |  7.63 |    0.24 | 0.2193 |     688 B |        6.14 |
| MediatR_Publish_N20_Honesty | 1,293.4 ns | 17.50 ns | 15.51 ns | 11.45 |    0.22 | 1.0185 |    3200 B |       28.57 |
