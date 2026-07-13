```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                    | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------- |----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_NoBehavior          |  91.53 ns | 29.28 ns | 1.605 ns |  1.00 |    0.02 | 0.0153 |      48 B |        1.00 |
| Rogue_NoBehavior_Concrete |  32.51 ns | 50.82 ns | 2.786 ns |  0.36 |    0.03 | 0.0153 |      48 B |        1.00 |
| MediatR_NoBehavior        | 105.10 ns | 47.12 ns | 2.583 ns |  1.15 |    0.03 | 0.0714 |     224 B |        4.67 |
