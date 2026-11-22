```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.115
  [Host]     : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3


```
| Method                    | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------- |---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_NoBehavior          | 84.07 ns | 0.733 ns | 0.612 ns |  1.00 |    0.01 | 0.0153 |      48 B |        1.00 |
| Rogue_NoBehavior_Concrete | 27.03 ns | 0.314 ns | 0.245 ns |  0.32 |    0.00 | 0.0153 |      48 B |        1.00 |
| MediatR_NoBehavior        | 98.89 ns | 1.333 ns | 1.113 ns |  1.18 |    0.02 | 0.0714 |     224 B |        4.67 |
