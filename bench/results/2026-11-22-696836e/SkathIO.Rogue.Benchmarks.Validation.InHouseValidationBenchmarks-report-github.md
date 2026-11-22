```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.115
  [Host]     : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3


```
| Method                   | Mean        | Error     | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------- |------------:|----------:|-----------:|------:|--------:|-------:|----------:|------------:|
| FluentValidation_Valid   | 2,517.68 ns | 19.495 ns |  17.282 ns |  1.00 |    0.01 | 1.2932 |    4064 B |        1.00 |
| RogueValidation_Valid    |    25.19 ns |  0.164 ns |   0.128 ns |  0.01 |    0.00 |      - |         - |        0.00 |
| FluentValidation_Invalid | 6,197.91 ns | 99.686 ns | 110.801 ns |  2.46 |    0.05 | 2.7161 |    8536 B |        2.10 |
| RogueValidation_Invalid  |   317.33 ns |  6.039 ns |   8.466 ns |  0.13 |    0.00 | 0.2828 |     888 B |        0.22 |
