```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method            | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------ |----------:|---------:|---------:|------:|--------:|---------:|----------:|------------:|
| Rogue_ColdStart   |  22.50 μs | 15.43 μs | 0.846 μs |  1.00 |    0.05 |   8.5144 |  26.17 KB |        1.00 |
| MediatR_ColdStart | 416.68 μs | 22.52 μs | 1.234 μs | 18.54 |    0.62 | 199.2188 | 611.52 KB |       23.36 |
