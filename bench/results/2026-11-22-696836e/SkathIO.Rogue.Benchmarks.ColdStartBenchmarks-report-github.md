```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.115
  [Host]     : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3


```
| Method            | Mean      | Error    | StdDev    | Ratio | RatioSD | Gen0     | Allocated | Alloc Ratio |
|------------------ |----------:|---------:|----------:|------:|--------:|---------:|----------:|------------:|
| Rogue_ColdStart   |  20.02 μs | 0.247 μs |  0.231 μs |  1.00 |    0.02 |   8.5144 |  26.17 KB |        1.00 |
| MediatR_ColdStart | 414.24 μs | 8.025 μs | 14.055 μs | 20.69 |    0.73 | 199.2188 | 611.51 KB |       23.36 |
