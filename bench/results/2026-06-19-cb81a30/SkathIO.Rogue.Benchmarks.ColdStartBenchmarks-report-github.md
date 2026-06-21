```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method            | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------ |----------:|---------:|---------:|------:|--------:|---------:|----------:|------------:|
| Rogue_ColdStart   |  20.65 μs | 14.05 μs | 0.770 μs |  1.00 |    0.05 |   8.3618 |  25.65 KB |        1.00 |
| MediatR_ColdStart | 398.98 μs | 14.75 μs | 0.808 μs | 19.34 |    0.63 | 201.6602 | 618.34 KB |       24.11 |
