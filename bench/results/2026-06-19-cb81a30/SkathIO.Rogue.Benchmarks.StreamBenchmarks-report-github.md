```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                     | Mean     | Error    | StdDev  | Gen0   | Allocated |
|--------------------------- |---------:|---------:|--------:|-------:|----------:|
| Rogue_CreateStream_10Items | 376.9 ns | 51.79 ns | 2.84 ns | 0.1097 |     344 B |
