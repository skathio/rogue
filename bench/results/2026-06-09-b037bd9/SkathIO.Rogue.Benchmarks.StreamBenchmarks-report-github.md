```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.108
  [Host]   : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                        | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------ |---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_CreateStream_10Items    | 415.1 ns | 230.5 ns | 12.64 ns |  1.00 |    0.04 | 0.1097 |     344 B |        1.00 |
| Mediator_CreateStream_10Items | 300.0 ns | 109.5 ns |  6.00 ns |  0.72 |    0.02 | 0.0687 |     216 B |        0.63 |
