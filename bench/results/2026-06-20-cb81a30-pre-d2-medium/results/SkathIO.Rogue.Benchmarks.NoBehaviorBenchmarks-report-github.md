```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]    : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2
  MediumRun : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2

Job=MediumRun  IterationCount=15  LaunchCount=2  
WarmupCount=10  

```
| Method                    | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------- |----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_NoBehavior          | 113.35 ns | 1.897 ns | 2.839 ns |  1.00 |    0.03 | 0.0153 |      48 B |        1.00 |
| Rogue_NoBehavior_Concrete |  31.63 ns | 1.197 ns | 1.755 ns |  0.28 |    0.02 | 0.0153 |      48 B |        1.00 |
| MediatR_NoBehavior        | 112.58 ns | 4.076 ns | 5.580 ns |  0.99 |    0.05 | 0.0714 |     224 B |        4.67 |
