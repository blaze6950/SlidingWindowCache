```

BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core i7-1065G7 CPU 1.30GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 8.0.418
  [Host]     : .NET 8.0.24 (8.0.2426.7010), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-BUXWGJ : .NET 8.0.24 (8.0.2426.7010), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

InvocationCount=1  UnrollFactor=1  

```
| Method        | Behavior      | Strategy       | BaseSpanSize |         Mean |       Error |      StdDev |          Gen0 |          Gen1 |          Gen2 |       Allocated |
|---------------|---------------|----------------|--------------|-------------:|------------:|------------:|--------------:|--------------:|--------------:|----------------:|
| **Rebalance** | **Fixed**     | **Snapshot**   | **100**      | **166.2 ms** | **3.17 ms** | **2.96 ms** |         **-** |         **-** |         **-** |   **199.03 KB** |
| **Rebalance** | **Fixed**     | **Snapshot**   | **1000**     | **164.6 ms** | **3.16 ms** | **3.64 ms** |         **-** |         **-** |         **-** |  **1677.78 KB** |
| **Rebalance** | **Fixed**     | **Snapshot**   | **10000**    | **162.3 ms** | **2.77 ms** | **3.88 ms** | **3000.0000** | **3000.0000** | **3000.0000** | **16445.87 KB** |
| **Rebalance** | **Fixed**     | **CopyOnRead** | **100**      | **165.9 ms** | **3.24 ms** | **3.98 ms** |         **-** |         **-** |         **-** |    **67.25 KB** |
| **Rebalance** | **Fixed**     | **CopyOnRead** | **1000**     | **166.0 ms** | **3.13 ms** | **4.39 ms** |         **-** |         **-** |         **-** |   **326.48 KB** |
| **Rebalance** | **Fixed**     | **CopyOnRead** | **10000**    | **162.9 ms** | **2.76 ms** | **3.28 ms** |         **-** |         **-** |         **-** |  **2470.11 KB** |
| **Rebalance** | **Growing**   | **Snapshot**   | **100**      | **166.2 ms** | **3.01 ms** | **3.09 ms** |         **-** |         **-** |         **-** |  **1162.11 KB** |
| **Rebalance** | **Growing**   | **Snapshot**   | **1000**     | **165.6 ms** | **3.31 ms** | **3.10 ms** |         **-** |         **-** |         **-** |  **2639.17 KB** |
| **Rebalance** | **Growing**   | **Snapshot**   | **10000**    | **159.7 ms** | **2.82 ms** | **3.25 ms** | **4000.0000** | **4000.0000** | **4000.0000** | **17407.75 KB** |
| **Rebalance** | **Growing**   | **CopyOnRead** | **100**      | **166.7 ms** | **3.31 ms** | **3.10 ms** |         **-** |         **-** |         **-** |   **755.79 KB** |
| **Rebalance** | **Growing**   | **CopyOnRead** | **1000**     | **166.1 ms** | **3.20 ms** | **3.28 ms** |         **-** |         **-** |         **-** |  **1078.92 KB** |
| **Rebalance** | **Growing**   | **CopyOnRead** | **10000**    | **164.3 ms** | **3.13 ms** | **4.28 ms** |         **-** |         **-** |         **-** |  **2710.51 KB** |
| **Rebalance** | **Shrinking** | **Snapshot**   | **100**      | **166.5 ms** | **3.21 ms** | **4.06 ms** |         **-** |         **-** |         **-** |    **918.7 KB** |
| **Rebalance** | **Shrinking** | **Snapshot**   | **1000**     | **164.8 ms** | **3.25 ms** | **3.61 ms** |         **-** |         **-** |         **-** |  **1720.91 KB** |
| **Rebalance** | **Shrinking** | **Snapshot**   | **10000**    | **162.4 ms** | **3.07 ms** | **4.40 ms** | **2000.0000** | **2000.0000** | **2000.0000** |  **9843.23 KB** |
| **Rebalance** | **Shrinking** | **CopyOnRead** | **100**      | **165.3 ms** | **3.30 ms** | **3.24 ms** |         **-** |         **-** |         **-** |   **654.09 KB** |
| **Rebalance** | **Shrinking** | **CopyOnRead** | **1000**     | **164.6 ms** | **3.16 ms** | **3.51 ms** |         **-** |         **-** |         **-** |  **1113.63 KB** |
| **Rebalance** | **Shrinking** | **CopyOnRead** | **10000**    | **161.4 ms** | **3.13 ms** | **4.78 ms** |         **-** |         **-** |         **-** |  **2745.21 KB** |
