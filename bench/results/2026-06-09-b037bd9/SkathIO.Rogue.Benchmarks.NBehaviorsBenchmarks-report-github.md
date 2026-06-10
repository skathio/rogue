```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.108
  [Host]   : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method           | Mean     | Error     | StdDev  | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------- |---------:|----------:|--------:|------:|--------:|-------:|----------:|------------:|
| Rogue_1Behavior  | 231.6 ns |  89.37 ns | 4.90 ns |  1.00 |    0.03 | 0.1221 |     384 B |        1.00 |
| Rogue_3Behaviors | 239.9 ns | 135.14 ns | 7.41 ns |  1.04 |    0.03 | 0.1221 |     384 B |        1.00 |
| Rogue_5Behaviors | 237.0 ns |  57.43 ns | 3.15 ns |  1.02 |    0.02 | 0.1223 |     384 B |        1.00 |
