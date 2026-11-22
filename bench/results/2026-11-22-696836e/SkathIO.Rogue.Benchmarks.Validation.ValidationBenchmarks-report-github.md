```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.115
  [Host]     : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3


```
| Method                | Mean     | Error    | StdDev   | Median   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------- |---------:|---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_ValidatedNoOp   | 521.4 ns |  7.72 ns |  6.45 ns | 518.8 ns |  1.00 |    0.02 | 0.3672 |   1.13 KB |        1.00 |
| MediatR_ValidatedNoOp | 543.8 ns | 12.16 ns | 33.29 ns | 530.3 ns |  1.04 |    0.06 | 0.4148 |   1.27 KB |        1.13 |
