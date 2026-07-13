```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                      | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------- |----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_SendObject_1Handler   |  80.40 ns | 56.98 ns | 3.123 ns |  1.00 |    0.05 | 0.0153 |      48 B |        1.00 |
| MediatR_SendObject          | 125.52 ns | 42.97 ns | 2.356 ns |  1.56 |    0.06 | 0.0942 |     296 B |        6.17 |
| Rogue_SendObject_25Handlers |  79.96 ns | 47.18 ns | 2.586 ns |  1.00 |    0.04 | 0.0153 |      48 B |        1.00 |
