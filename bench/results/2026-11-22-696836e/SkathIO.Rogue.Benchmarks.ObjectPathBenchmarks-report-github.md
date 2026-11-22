```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.115
  [Host]     : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3


```
| Method                      | Mean      | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------- |----------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_SendObject_1Handler   |  72.29 ns | 0.654 ns | 0.580 ns |  1.00 |    0.01 | 0.0153 |      48 B |        1.00 |
| MediatR_SendObject          | 114.64 ns | 1.148 ns | 0.959 ns |  1.59 |    0.02 | 0.0942 |     296 B |        6.17 |
| Rogue_SendObject_25Handlers |  72.77 ns | 0.630 ns | 0.526 ns |  1.01 |    0.01 | 0.0153 |      48 B |        1.00 |
