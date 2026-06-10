```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.108
  [Host]   : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                      | Mean      | Error      | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------- |----------:|-----------:|---------:|------:|--------:|-------:|----------:|------------:|
| Rogue_SendObject_1Handler   | 224.84 ns | 120.122 ns | 6.584 ns |  1.00 |    0.04 | 0.1223 |     384 B |        1.00 |
| MediatR_SendObject          | 123.46 ns |  66.680 ns | 3.655 ns |  0.55 |    0.02 | 0.0942 |     296 B |        0.77 |
| Mediator_SendObject         |  21.86 ns |   1.471 ns | 0.081 ns |  0.10 |    0.00 | 0.0076 |      24 B |        0.06 |
| Rogue_SendObject_25Handlers | 225.65 ns | 123.711 ns | 6.781 ns |  1.00 |    0.04 | 0.1221 |     384 B |        1.00 |
