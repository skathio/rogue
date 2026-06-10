```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.108
  [Host]   : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method              | Mean      | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------- |----------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_NoBehavior    | 245.76 ns | 154.46 ns | 8.466 ns |  1.00 |    0.04 | 0.1221 |     384 B |        1.00 |
| MediatR_NoBehavior  | 107.31 ns |  67.34 ns | 3.691 ns |  0.44 |    0.02 | 0.0714 |     224 B |        0.58 |
| Mediator_NoBehavior |  17.98 ns |  12.06 ns | 0.661 ns |  0.07 |    0.00 | 0.0076 |      24 B |        0.06 |
