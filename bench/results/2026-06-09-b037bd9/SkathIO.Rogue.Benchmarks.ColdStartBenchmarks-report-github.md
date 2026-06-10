```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.108
  [Host]   : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method             | Mean      | Error      | StdDev    | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------- |----------:|-----------:|----------:|------:|--------:|---------:|----------:|------------:|
| Rogue_ColdStart    |  14.21 μs |   4.512 μs |  0.247 μs |  1.00 |    0.02 |   8.7280 |  26.74 KB |        1.00 |
| MediatR_ColdStart  | 567.24 μs | 219.076 μs | 12.008 μs | 39.91 |    0.95 | 261.7188 | 804.48 KB |       30.09 |
| Mediator_ColdStart |  63.05 μs |  50.679 μs |  2.778 μs |  4.44 |    0.18 |  24.0479 |  73.82 KB |        2.76 |
