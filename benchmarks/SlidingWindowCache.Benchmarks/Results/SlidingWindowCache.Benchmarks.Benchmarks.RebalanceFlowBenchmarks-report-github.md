```

BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core i7-1065G7 CPU 1.30GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 8.0.418
  [Host]     : .NET 8.0.24 (8.0.2426.7010), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-RLYSTP : .NET 8.0.24 (8.0.2426.7010), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

InvocationCount=1  UnrollFactor=1  

```
| Method        | Behavior      | Strategy       | BaseSpanSize |         Mean |       Error |      StdDev |          Gen0 |          Gen1 |          Gen2 |       Allocated |
|---------------|---------------|----------------|--------------|-------------:|------------:|------------:|--------------:|--------------:|--------------:|----------------:|
| **Rebalance** | **Fixed**     | **Snapshot**   | **100**      | **166.3 ms** | **3.11 ms** | **3.05 ms** |         **-** |         **-** |         **-** |   **198.18 KB** |
| **Rebalance** | **Fixed**     | **Snapshot**   | **1000**     | **165.7 ms** | **3.16 ms** | **3.25 ms** |         **-** |         **-** |         **-** |  **1676.93 KB** |
| **Rebalance** | **Fixed**     | **Snapshot**   | **10000**    | **163.8 ms** | **3.24 ms** | **3.60 ms** | **3000.0000** | **3000.0000** | **3000.0000** | **16445.02 KB** |
| **Rebalance** | **Fixed**     | **CopyOnRead** | **100**      | **166.4 ms** | **3.23 ms** | **3.72 ms** |         **-** |         **-** |         **-** |    **66.12 KB** |
| **Rebalance** | **Fixed**     | **CopyOnRead** | **1000**     | **166.4 ms** | **3.25 ms** | **3.48 ms** |         **-** |         **-** |         **-** |   **325.63 KB** |
| **Rebalance** | **Fixed**     | **CopyOnRead** | **10000**    | **162.6 ms** | **3.19 ms** | **3.54 ms** |         **-** |         **-** |         **-** |  **2469.26 KB** |
| **Rebalance** | **Growing**   | **Snapshot**   | **100**      | **166.9 ms** | **3.30 ms** | **3.80 ms** |         **-** |         **-** |         **-** |   **940.55 KB** |
| **Rebalance** | **Growing**   | **Snapshot**   | **1000**     | **167.4 ms** | **3.28 ms** | **4.27 ms** |         **-** |         **-** |         **-** |  **2417.61 KB** |
| **Rebalance** | **Growing**   | **Snapshot**   | **10000**    | **164.9 ms** | **3.26 ms** | **4.77 ms** | **3000.0000** | **3000.0000** | **3000.0000** |  **17185.6 KB** |
| **Rebalance** | **Growing**   | **CopyOnRead** | **100**      | **166.3 ms** | **3.21 ms** | **3.44 ms** |         **-** |         **-** |         **-** |   **534.23 KB** |
| **Rebalance** | **Growing**   | **CopyOnRead** | **1000**     | **166.5 ms** | **3.25 ms** | **3.04 ms** |         **-** |         **-** |         **-** |   **857.36 KB** |
| **Rebalance** | **Growing**   | **CopyOnRead** | **10000**    | **165.4 ms** | **3.27 ms** | **4.37 ms** |         **-** |         **-** |         **-** |  **2488.95 KB** |
| **Rebalance** | **Shrinking** | **Snapshot**   | **100**      | **166.0 ms** | **3.03 ms** | **3.11 ms** |         **-** |         **-** |         **-** |    **661.5 KB** |
| **Rebalance** | **Shrinking** | **Snapshot**   | **1000**     | **165.7 ms** | **3.25 ms** | **4.45 ms** |         **-** |         **-** |         **-** |  **1463.66 KB** |
| **Rebalance** | **Shrinking** | **Snapshot**   | **10000**    | **163.2 ms** | **3.14 ms** | **4.19 ms** | **1000.0000** | **1000.0000** | **1000.0000** |  **9585.38 KB** |
| **Rebalance** | **Shrinking** | **CopyOnRead** | **100**      | **166.0 ms** | **3.25 ms** | **3.47 ms** |         **-** |         **-** |         **-** |   **397.81 KB** |
| **Rebalance** | **Shrinking** | **CopyOnRead** | **1000**     | **166.0 ms** | **3.19 ms** | **3.13 ms** |         **-** |         **-** |         **-** |   **856.37 KB** |
| **Rebalance** | **Shrinking** | **CopyOnRead** | **10000**    | **162.2 ms** | **3.01 ms** | **2.82 ms** |         **-** |         **-** |         **-** |  **2487.95 KB** |
