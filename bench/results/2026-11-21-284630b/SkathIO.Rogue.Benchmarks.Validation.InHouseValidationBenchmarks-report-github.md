```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.115
  [Host]   : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                   | Mean        | Error        | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------- |------------:|-------------:|-----------:|------:|--------:|-------:|----------:|------------:|
| FluentValidation_Valid   | 3,071.08 ns | 2,700.731 ns | 148.036 ns | 1.002 |    0.06 | 1.2932 |    4064 B |        1.00 |
| RogueValidation_Valid    |    28.96 ns |     7.318 ns |   0.401 ns | 0.009 |    0.00 |      - |         - |        0.00 |
| FluentValidation_Invalid | 6,312.10 ns |   619.327 ns |  33.947 ns | 2.059 |    0.09 | 2.7161 |    8536 B |        2.10 |
| RogueValidation_Invalid  |   326.10 ns |    32.976 ns |   1.808 ns | 0.106 |    0.00 | 0.2828 |     888 B |        0.22 |
