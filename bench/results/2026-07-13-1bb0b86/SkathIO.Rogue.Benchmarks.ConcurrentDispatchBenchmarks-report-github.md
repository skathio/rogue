```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Max: 0.80GHz) (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method         | Concurrency | Mean        | Error      | StdDev    | Completed Work Items | Lock Contentions | Gen0    | Allocated |
|--------------- |------------ |------------:|-----------:|----------:|---------------------:|-----------------:|--------:|----------:|
| **ConcurrentSend** | **1**           |    **970.4 ns** |   **501.2 ns** |  **27.47 ns** |                    **-** |                **-** |  **0.8850** |   **2.71 KB** |
| **ConcurrentSend** | **4**           |  **3,625.3 ns** |   **747.0 ns** |  **40.95 ns** |                    **-** |                **-** |  **3.3607** |   **10.3 KB** |
| **ConcurrentSend** | **8**           |  **7,143.0 ns** | **3,410.5 ns** | **186.94 ns** |                    **-** |                **-** |  **6.6681** |  **20.43 KB** |
| **ConcurrentSend** | **16**          | **14,323.5 ns** | **7,356.9 ns** | **403.25 ns** |                    **-** |                **-** | **13.2751** |  **40.68 KB** |
