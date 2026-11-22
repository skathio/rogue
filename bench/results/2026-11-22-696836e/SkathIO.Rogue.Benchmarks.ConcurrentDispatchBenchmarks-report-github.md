```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.115
  [Host]     : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 RyuJIT x86-64-v3


```
| Method         | Concurrency | Mean        | Error     | StdDev    | Gen0    | Completed Work Items | Lock Contentions | Allocated |
|--------------- |------------ |------------:|----------:|----------:|--------:|---------------------:|-----------------:|----------:|
| **ConcurrentSend** | **1**           |    **886.2 ns** |  **15.41 ns** |  **13.66 ns** |  **0.8850** |                    **-** |                **-** |   **2.71 KB** |
| **ConcurrentSend** | **4**           |  **3,241.4 ns** |  **64.21 ns** | **120.60 ns** |  **3.3607** |                    **-** |                **-** |   **10.3 KB** |
| **ConcurrentSend** | **8**           |  **6,372.6 ns** | **119.27 ns** | **105.73 ns** |  **6.6681** |                    **-** |                **-** |  **20.43 KB** |
| **ConcurrentSend** | **16**          | **13,965.8 ns** | **239.01 ns** | **211.87 ns** | **13.2751** |                    **-** |                **-** |  **40.68 KB** |
