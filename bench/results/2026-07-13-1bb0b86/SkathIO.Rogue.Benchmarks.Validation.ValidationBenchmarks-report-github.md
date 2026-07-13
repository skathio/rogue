```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 3.10GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------- |---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_ValidatedNoOp   | 549.4 ns | 178.0 ns |  9.76 ns |  1.00 |    0.02 | 0.3672 |   1.13 KB |        1.00 |
| MediatR_ValidatedNoOp | 579.0 ns | 220.5 ns | 12.08 ns |  1.05 |    0.03 | 0.4148 |   1.27 KB |        1.13 |
