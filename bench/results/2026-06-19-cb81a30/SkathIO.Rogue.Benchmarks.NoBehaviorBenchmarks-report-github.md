```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                    | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------- |----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_NoBehavior          |  93.96 ns | 84.12 ns | 4.611 ns |  1.00 |    0.06 | 0.0153 |      48 B |        1.00 |
| Rogue_NoBehavior_Concrete |  30.58 ns | 10.58 ns | 0.580 ns |  0.33 |    0.01 | 0.0153 |      48 B |        1.00 |
| MediatR_NoBehavior        | 114.71 ns | 27.16 ns | 1.489 ns |  1.22 |    0.05 | 0.0713 |     224 B |        4.67 |
