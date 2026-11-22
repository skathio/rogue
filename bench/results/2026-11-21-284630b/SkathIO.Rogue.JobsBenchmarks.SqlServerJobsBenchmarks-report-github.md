```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.115
  [Host]   : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                 | Jobs | Mean    | Error   | StdDev   | Ratio | RatioSD | Gen0       | Gen1      | Allocated | Alloc Ratio |
|--------------------------------------- |----- |--------:|--------:|---------:|------:|--------:|-----------:|----------:|----------:|------------:|
| Hangfire_EnqueueEach_AndRun            | 1000 | 3.191 m | 1.330 m | 0.0729 m |  1.00 |    0.03 | 45000.0000 | 3000.0000 | 134.44 MB |        1.00 |
| Rogue_EnqueueEach_AndRun               | 1000 | 1.994 m | 2.956 m | 0.1620 m |  0.63 |    0.05 |  9000.0000 |         - |  29.33 MB |        0.22 |
| Rogue_EnqueueMany_AndRun               | 1000 | 1.717 m | 1.679 m | 0.0920 m |  0.54 |    0.03 |  6000.0000 |         - |  18.55 MB |        0.14 |
| Rogue_EnqueueMany_ScopePerBatch_AndRun | 1000 | 1.698 m | 2.515 m | 0.1379 m |  0.53 |    0.04 |  6000.0000 |         - |  18.61 MB |        0.14 |
