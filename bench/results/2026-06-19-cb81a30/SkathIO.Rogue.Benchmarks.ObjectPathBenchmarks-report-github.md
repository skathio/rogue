```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                      | Mean      | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------- |----------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_SendObject_1Handler   |  83.46 ns |  67.12 ns | 3.679 ns |  1.00 |    0.05 | 0.0153 |      48 B |        1.00 |
| MediatR_SendObject          | 120.98 ns |  51.90 ns | 2.845 ns |  1.45 |    0.06 | 0.0942 |     296 B |        6.17 |
| Rogue_SendObject_25Handlers |  88.09 ns | 114.18 ns | 6.259 ns |  1.06 |    0.08 | 0.0153 |      48 B |        1.00 |
