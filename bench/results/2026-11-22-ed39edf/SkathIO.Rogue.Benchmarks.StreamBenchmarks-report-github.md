```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.115
  [Host]     : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3


```
| Method                       | Mean     | Error   | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------- |---------:|--------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_CreateStream_10Items   | 362.0 ns | 6.59 ns | 10.07 ns |  1.00 |    0.04 | 0.1097 |     344 B |        1.00 |
| MediatR_CreateStream_10Items | 683.6 ns | 5.08 ns |  7.28 ns |  1.89 |    0.05 | 0.1936 |     608 B |        1.77 |
