```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.109
  [Host]   : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method         | Concurrency | Mean        | Error      | StdDev    | Completed Work Items | Lock Contentions | Gen0    | Allocated |
|--------------- |------------ |------------:|-----------:|----------:|---------------------:|-----------------:|--------:|----------:|
| **ConcurrentSend** | **1**           |    **871.6 ns** |   **341.2 ns** |  **18.70 ns** |                    **-** |                **-** |  **0.8821** |    **2.7 KB** |
| **ConcurrentSend** | **4**           |  **3,614.1 ns** | **3,833.3 ns** | **210.12 ns** |                    **-** |                **-** |  **3.3531** |  **10.27 KB** |
| **ConcurrentSend** | **8**           |  **7,096.8 ns** | **4,515.4 ns** | **247.50 ns** |                    **-** |                **-** |  **6.6452** |  **20.37 KB** |
| **ConcurrentSend** | **16**          | **13,617.9 ns** | **2,816.2 ns** | **154.37 ns** |                    **-** |                **-** | **13.2294** |  **40.55 KB** |
