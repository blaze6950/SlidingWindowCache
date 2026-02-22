```

BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core i7-1065G7 CPU 1.30GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 8.0.418
  [Host]     : .NET 8.0.24 (8.0.2426.7010), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-PMDJXO : .NET 8.0.24 (8.0.2426.7010), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

InvocationCount=1  UnrollFactor=1  

```
| Method                                    | RangeSpan | CacheCoefficientSize |                Mean |            Error |           StdDev |           Median |    Ratio |  RatioSD |       Allocated | Alloc Ratio |
|-------------------------------------------|-----------|----------------------|--------------------:|-----------------:|-----------------:|-----------------:|---------:|---------:|----------------:|------------:|
| **User_FullHit_Snapshot**                 | **100**   | **1**                |        **31.26 μs** |     **3.280 μs** |     **9.411 μs** |     **29.10 μs** | **1.00** | **0.00** |     **1.32 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 100       | 1                    |            34.46 μs |         3.526 μs |        10.173 μs |         30.80 μs |     1.12 |     0.22 |         2.06 KB |        1.56 |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **100**   | **10**               |        **26.02 μs** |     **3.172 μs** |     **8.946 μs** |     **24.10 μs** | **1.00** | **0.00** |     **1.32 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 100       | 10                   |            45.92 μs |         7.613 μs |        22.085 μs |         30.15 μs |     1.98 |     1.16 |         6.32 KB |        4.79 |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **100**   | **100**              |        **26.10 μs** |     **2.118 μs** |     **5.975 μs** |     **26.40 μs** | **1.00** | **0.00** |     **1.32 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 100       | 100                  |            70.55 μs |         7.519 μs |        22.053 μs |         78.00 μs |     2.75 |     0.60 |        48.93 KB |       37.06 |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **1000**  | **1**                |        **28.11 μs** |     **3.000 μs** |     **8.313 μs** |     **26.00 μs** | **1.00** | **0.00** |     **1.32 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 1000      | 1                    |            51.49 μs |         8.242 μs |        23.912 μs |         57.60 μs |     1.96 |     0.80 |         8.39 KB |        6.36 |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **1000**  | **10**               |        **26.66 μs** |     **2.224 μs** |     **6.236 μs** |     **28.20 μs** | **1.00** | **0.00** |     **1.32 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 1000      | 10                   |            74.43 μs |         8.027 μs |        23.414 μs |         83.30 μs |     2.90 |     0.80 |        50.62 KB |       38.34 |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **1000**  | **100**              |        **26.31 μs** |     **2.547 μs** |     **7.266 μs** |     **24.30 μs** | **1.00** | **0.00** |     **1.32 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 1000      | 100                  |           288.42 μs |        26.812 μs |        78.636 μs |        294.10 μs |    11.77 |     4.11 |       472.91 KB |      358.18 |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **10000** | **1**                |        **15.74 μs** |     **2.110 μs** |     **6.121 μs** |     **14.50 μs** | **1.00** | **0.00** |     **1.32 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 10000     | 1                    |            47.63 μs |         5.995 μs |        17.391 μs |         44.20 μs |     3.22 |     1.10 |        71.67 KB |       54.28 |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **10000** | **10**               |        **18.11 μs** |     **2.417 μs** |     **6.936 μs** |     **17.70 μs** | **1.00** | **0.00** |     **1.32 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 10000     | 10                   |           321.96 μs |        21.435 μs |        62.864 μs |        335.40 μs |    20.19 |     7.70 |       493.59 KB |      373.84 |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **10000** | **100**              |        **13.65 μs** |     **1.139 μs** |     **3.041 μs** |     **14.60 μs** | **1.00** | **0.00** |     **1.32 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 10000     | 100                  |         1,627.24 μs |       241.090 μs |       710.858 μs |      1,228.45 μs |   131.10 |    61.19 |      4712.76 KB |    3,569.43 |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **100**   | **1**                |        **42.82 μs** |     **2.507 μs** |     **6.693 μs** |     **42.50 μs** |    **?** |    **?** |     **6.47 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 100       | 1                    |            44.97 μs |         3.070 μs |         8.351 μs |         44.00 μs |        ? |        ? |         6.47 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **100**   | **10**               |        **66.30 μs** |     **1.320 μs** |     **3.262 μs** |     **66.35 μs** |    **?** |    **?** |    **27.64 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 100       | 10                   |            66.02 μs |         1.802 μs |         4.841 μs |         66.05 μs |        ? |        ? |        27.64 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **100**   | **100**              |       **244.85 μs** |    **12.346 μs** |    **33.378 μs** |    **252.80 μs** |    **?** |    **?** |   **210.88 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 100       | 100                  |           258.13 μs |         9.359 μs |        25.935 μs |        261.90 μs |        ? |        ? |       210.88 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **1000**  | **1**                |        **71.30 μs** |     **2.052 μs** |     **5.442 μs** |     **69.90 μs** |    **?** |    **?** |    **31.09 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 1000      | 1                    |            71.73 μs |         2.411 μs |         6.519 μs |         71.55 μs |        ? |        ? |        31.09 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **1000**  | **10**               |       **126.31 μs** |     **8.422 μs** |    **22.769 μs** |    **122.60 μs** |    **?** |    **?** |   **212.63 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 1000      | 10                   |           140.75 μs |        11.412 μs |        31.813 μs |        144.25 μs |        ? |        ? |       213.69 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **1000**  | **100**              |       **932.72 μs** |    **49.104 μs** |   **135.247 μs** |    **881.25 μs** |    **?** |    **?** |  **1813.59 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 1000      | 100                  |         1,843.16 μs |       209.596 μs |       584.269 μs |      2,114.05 μs |        ? |        ? |      1812.09 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **10000** | **1**                |       **325.50 μs** |    **21.469 μs** |    **58.408 μs** |    **352.15 μs** |    **?** |    **?** |   **248.77 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 10000     | 1                    |           345.79 μs |         6.858 μs |        18.067 μs |        348.80 μs |        ? |        ? |       248.77 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **10000** | **10**               |     **2,084.77 μs** |   **150.453 μs** |   **398.979 μs** |  **2,221.20 μs** |    **?** |    **?** |  **1848.04 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 10000     | 10                   |         2,129.79 μs |       106.833 μs |       277.674 μs |      2,227.50 μs |        ? |        ? |      1848.04 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **10000** | **100**              |     **8,709.28 μs** |   **691.244 μs** | **1,845.070 μs** |  **7,924.45 μs** |    **?** |    **?** | **16048.36 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 10000     | 100                  |         9,873.87 μs |       885.900 μs |     2,454.824 μs |      9,722.10 μs |        ? |        ? |     16046.84 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **100**   | **1**                |        **64.46 μs** |     **5.562 μs** |    **15.412 μs** |     **61.40 μs** |    **?** |    **?** |     **6.35 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 100       | 1                    |            60.24 μs |         3.333 μs |         8.723 μs |         60.05 μs |        ? |        ? |         6.36 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 100       | 1                    |            52.74 μs |         1.789 μs |         4.744 μs |         52.60 μs |        ? |        ? |          6.3 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 100       | 1                    |            64.81 μs |         6.651 μs |        19.294 μs |         56.90 μs |        ? |        ? |         6.92 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **100**   | **10**               |       **121.93 μs** |     **3.800 μs** |    **10.403 μs** |    **120.95 μs** |    **?** |    **?** |    **20.63 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 100       | 10                   |           128.64 μs |         5.914 μs |        15.265 μs |        126.95 μs |        ? |        ? |        20.63 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 100       | 10                   |            91.33 μs |         2.236 μs |         5.929 μs |         90.65 μs |        ? |        ? |        20.57 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 100       | 10                   |           102.66 μs |         3.812 μs |         9.907 μs |         99.80 μs |        ? |        ? |        20.58 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **100**   | **100**              |       **715.70 μs** |    **17.401 μs** |    **46.746 μs** |    **724.70 μs** |    **?** |    **?** |   **161.38 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 100       | 100                  |           786.19 μs |        15.678 μs |        39.907 μs |        789.30 μs |        ? |        ? |       162.88 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 100       | 100                  |           539.84 μs |        23.799 μs |        64.747 μs |        552.15 μs |        ? |        ? |       162.82 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 100       | 100                  |           504.87 μs |        19.855 μs |        52.306 μs |        511.70 μs |        ? |        ? |       162.83 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **1000**  | **1**                |       **132.28 μs** |     **3.258 μs** |     **8.640 μs** |    **131.25 μs** |    **?** |    **?** |    **27.52 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 1000      | 1                    |           158.52 μs |         2.790 μs |         6.297 μs |        157.55 μs |        ? |        ? |        27.52 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 1000      | 1                    |           119.84 μs |         2.836 μs |         7.569 μs |        119.00 μs |        ? |        ? |        27.46 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 1000      | 1                    |           115.82 μs |         2.687 μs |         7.031 μs |        114.55 μs |        ? |        ? |        27.47 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **1000**  | **10**               |       **578.40 μs** |    **11.398 μs** |    **25.494 μs** |    **580.30 μs** |    **?** |    **?** |    **168.5 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 1000      | 10                   |           866.30 μs |        44.396 μs |       129.505 μs |        794.85 μs |        ? |        ? |       168.51 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 1000      | 10                   |           417.43 μs |        12.077 μs |        32.651 μs |        424.30 μs |        ? |        ? |       168.45 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 1000      | 10                   |           501.60 μs |        11.092 μs |        28.631 μs |        506.40 μs |        ? |        ? |       168.45 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **1000**  | **100**              |     **5,982.06 μs** |   **494.680 μs** | **1,458.576 μs** |  **6,578.30 μs** |    **?** |    **?** |  **1576.25 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 1000      | 100                  |         7,914.86 μs |       526.029 μs |     1,551.009 μs |      8,492.20 μs |        ? |        ? |      1576.23 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 1000      | 100                  |         4,469.76 μs |       349.830 μs |     1,031.482 μs |      4,843.75 μs |        ? |        ? |      1576.17 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 1000      | 100                  |         3,866.99 μs |       452.560 μs |     1,192.225 μs |      4,546.70 μs |        ? |        ? |      1574.69 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **10000** | **1**                |       **807.67 μs** |    **12.108 μs** |    **21.522 μs** |    **809.00 μs** |    **?** |    **?** |   **238.67 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 10000     | 1                    |         1,097.37 μs |        25.335 μs |        64.024 μs |      1,100.30 μs |        ? |        ? |       238.68 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 10000     | 1                    |           593.11 μs |        17.900 μs |        48.395 μs |        597.70 μs |        ? |        ? |       238.62 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 10000     | 1                    |           675.89 μs |         4.438 μs |        10.018 μs |        674.70 μs |        ? |        ? |       238.63 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **10000** | **10**               |     **6,705.68 μs** |   **348.699 μs** | **1,022.673 μs** |  **6,946.60 μs** |    **?** |    **?** |  **1645.13 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 10000     | 10                   |         8,066.30 μs |       388.037 μs |     1,138.046 μs |      8,305.40 μs |        ? |        ? |      1643.65 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 10000     | 10                   |         4,519.36 μs |       297.315 μs |       867.283 μs |      4,834.05 μs |        ? |        ? |      1643.81 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 10000     | 10                   |         4,693.33 μs |       229.131 μs |       611.598 μs |      4,767.70 μs |        ? |        ? |      1645.09 KB |           ? |
|                                           |           |                      |                     |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **10000** | **100**              |    **27,022.21 μs** | **1,189.747 μs** | **3,432.693 μs** | **25,733.55 μs** |    **?** |    **?** | **15708.63 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 10000     | 100                  |        35,055.92 μs |     2,298.232 μs |     6,740.316 μs |     32,342.90 μs |        ? |        ? |     15708.15 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 10000     | 100                  |        20,446.49 μs |     1,155.748 μs |     3,297.415 μs |     19,069.30 μs |        ? |        ? |     15707.95 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 10000     | 100                  |        23,373.30 μs |     1,962.415 μs |     5,786.225 μs |     22,798.40 μs |        ? |        ? |     15708.59 KB |           ? |
