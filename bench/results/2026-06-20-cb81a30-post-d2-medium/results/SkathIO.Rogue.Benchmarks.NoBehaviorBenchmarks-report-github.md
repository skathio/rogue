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
| Rogue_NoBehavior          |  90.64 ns | 1.637 ns | 2.348 ns |  1.00 |    0.04 | 0.0153 |      48 B |        1.00 |
| Rogue_NoBehavior_Concrete |  30.50 ns | 0.557 ns | 0.762 ns |  0.34 |    0.01 | 0.0153 |      48 B |        1.00 |
| MediatR_NoBehavior        | 110.62 ns | 2.765 ns | 4.053 ns |  1.22 |    0.05 | 0.0714 |     224 B |        4.67 |
