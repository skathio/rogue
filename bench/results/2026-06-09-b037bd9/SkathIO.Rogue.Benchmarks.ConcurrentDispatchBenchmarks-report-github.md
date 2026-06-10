```

BenchmarkDotNet v0.15.2, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Core i7-6700HQ CPU 2.60GHz (Skylake), 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.108
  [Host]   : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.8 (10.0.826.23019), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method         | Concurrency | Mean       | Error      | StdDev    | Gen0   | Completed Work Items | Lock Contentions | Allocated |
|--------------- |------------ |-----------:|-----------:|----------:|-------:|---------------------:|-----------------:|----------:|
| **ConcurrentSend** | **1**           |   **617.3 ns** |   **386.0 ns** |  **21.16 ns** | **0.3309** |                    **-** |                **-** |   **1.02 KB** |
| **ConcurrentSend** | **4**           | **2,110.1 ns** | **1,039.8 ns** |  **56.99 ns** | **1.1482** |                    **-** |                **-** |   **3.52 KB** |
| **ConcurrentSend** | **8**           | **4,144.5 ns** |   **619.7 ns** |  **33.97 ns** | **2.2354** |                    **-** |                **-** |   **6.87 KB** |
| **ConcurrentSend** | **16**          | **8,180.0 ns** | **9,420.0 ns** | **516.34 ns** | **4.4250** |                    **-** |                **-** |  **13.55 KB** |
