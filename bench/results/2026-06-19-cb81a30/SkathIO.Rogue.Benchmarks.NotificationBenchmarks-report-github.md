```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                      | Mean       | Error       | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------- |-----------:|------------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_Publish_N2            |   196.6 ns |   255.10 ns | 13.98 ns |  1.00 |    0.09 | 0.1223 |     384 B |        1.00 |
| MediatR_Publish_N2          |   221.1 ns |   189.95 ns | 10.41 ns |  1.13 |    0.08 | 0.1478 |     464 B |        1.21 |
| Rogue_Publish_N5            |   417.7 ns |   240.60 ns | 13.19 ns |  2.13 |    0.14 | 0.2675 |     840 B |        2.19 |
| MediatR_Publish_N5          |   389.5 ns |    67.81 ns |  3.72 ns |  1.99 |    0.12 | 0.2933 |     920 B |        2.40 |
| Rogue_Publish_N20_Honesty   | 1,677.4 ns | 1,168.62 ns | 64.06 ns |  8.56 |    0.59 | 0.9937 |    3120 B |        8.12 |
| MediatR_Publish_N20_Honesty | 1,354.3 ns |   126.22 ns |  6.92 ns |  6.91 |    0.42 | 1.0185 |    3200 B |        8.33 |
