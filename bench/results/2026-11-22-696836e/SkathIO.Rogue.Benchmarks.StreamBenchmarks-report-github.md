```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.115
  [Host]     : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3


```
| Method                     | Mean     | Error   | StdDev  | Gen0   | Allocated |
|--------------------------- |---------:|--------:|--------:|-------:|----------:|
| Rogue_CreateStream_10Items | 361.9 ns | 2.30 ns | 1.80 ns | 0.1097 |     344 B |
