```

BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core i7-1065G7 CPU 1.30GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 8.0.403
  [Host]     : .NET 8.0.11 (8.0.1124.51707), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-OPIWYK : .NET 8.0.11 (8.0.1124.51707), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

InvocationCount=1  UnrollFactor=1  

```
| Method                                   | RangeSpan | CacheCoefficientSize | Mean         | Error        | StdDev       | Median       | Ratio  | RatioSD | Allocated   | Alloc Ratio |
|----------------------------------------- |---------- |--------------------- |-------------:|-------------:|-------------:|-------------:|-------:|--------:|------------:|------------:|
| **User_FullHit_Snapshot**                    | **100**       | **1**                    |     **28.48 μs** |     **2.805 μs** |     **7.726 μs** |     **28.25 μs** |   **1.00** |    **0.00** |     **1.77 KB** |        **1.00** |
| User_FullHit_CopyOnRead                  | 100       | 1                    |     37.16 μs |     5.201 μs |    15.172 μs |     37.90 μs |   1.37 |    0.46 |     2.51 KB |        1.42 |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullHit_Snapshot**                    | **100**       | **10**                   |     **25.72 μs** |     **2.020 μs** |     **5.598 μs** |     **22.20 μs** |   **1.00** |    **0.00** |     **1.77 KB** |        **1.00** |
| User_FullHit_CopyOnRead                  | 100       | 10                   |     47.16 μs |     8.119 μs |    23.294 μs |     54.30 μs |   1.82 |    0.70 |     6.77 KB |        3.83 |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullHit_Snapshot**                    | **100**       | **100**                  |     **25.93 μs** |     **2.438 μs** |     **6.756 μs** |     **26.20 μs** |   **1.00** |    **0.00** |     **1.77 KB** |        **1.00** |
| User_FullHit_CopyOnRead                  | 100       | 100                  |     71.48 μs |     7.908 μs |    23.067 μs |     78.00 μs |   2.84 |    0.61 |    49.38 KB |       27.96 |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullHit_Snapshot**                    | **1000**      | **1**                    |     **28.51 μs** |     **3.773 μs** |    **10.517 μs** |     **28.55 μs** |   **1.00** |    **0.00** |     **1.77 KB** |        **1.00** |
| User_FullHit_CopyOnRead                  | 1000      | 1                    |     47.99 μs |     8.341 μs |    24.330 μs |     54.10 μs |   1.76 |    0.66 |     8.84 KB |        5.00 |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullHit_Snapshot**                    | **1000**      | **10**                   |     **24.74 μs** |     **2.854 μs** |     **7.861 μs** |     **25.45 μs** |   **1.00** |    **0.00** |     **1.77 KB** |        **1.00** |
| User_FullHit_CopyOnRead                  | 1000      | 10                   |     71.17 μs |     7.872 μs |    22.964 μs |     76.75 μs |   3.12 |    0.98 |    51.06 KB |       28.92 |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullHit_Snapshot**                    | **1000**      | **100**                  |     **20.91 μs** |     **3.697 μs** |    **10.489 μs** |     **17.15 μs** |   **1.00** |    **0.00** |     **1.77 KB** |        **1.00** |
| User_FullHit_CopyOnRead                  | 1000      | 100                  |    153.77 μs |    10.768 μs |    30.895 μs |    150.45 μs |   8.89 |    3.74 |   473.08 KB |      267.94 |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullHit_Snapshot**                    | **10000**     | **1**                    |     **14.91 μs** |     **2.769 μs** |     **7.810 μs** |     **13.30 μs** |   **1.00** |    **0.00** |     **1.77 KB** |        **1.00** |
| User_FullHit_CopyOnRead                  | 10000     | 1                    |     63.34 μs |     7.619 μs |    22.224 μs |     62.70 μs |   4.99 |    2.16 |    72.12 KB |       40.85 |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullHit_Snapshot**                    | **10000**     | **10**                   |     **30.79 μs** |     **8.644 μs** |    **25.487 μs** |     **15.95 μs** |   **1.00** |    **0.00** |     **1.77 KB** |        **1.00** |
| User_FullHit_CopyOnRead                  | 10000     | 10                   |    193.62 μs |    10.014 μs |    28.893 μs |    196.80 μs |  12.00 |    8.52 |   494.03 KB |      279.81 |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullHit_Snapshot**                    | **10000**     | **100**                  |     **16.87 μs** |     **4.122 μs** |    **11.143 μs** |     **13.70 μs** |   **1.00** |    **0.00** |     **1.77 KB** |        **1.00** |
| User_FullHit_CopyOnRead                  | 10000     | 100                  |  1,574.74 μs |   203.654 μs |   600.478 μs |  1,258.85 μs | 124.15 |   72.36 |   4713.2 KB |    2,669.42 |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullMiss_Snapshot**                   | **100**       | **1**                    |     **37.90 μs** |     **5.039 μs** |    **13.794 μs** |     **39.40 μs** |      **?** |       **?** |     **5.45 KB** |           **?** |
| User_FullMiss_CopyOnRead                 | 100       | 1                    |     40.12 μs |     2.281 μs |     6.089 μs |     39.20 μs |      ? |       ? |     5.45 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullMiss_Snapshot**                   | **100**       | **10**                   |     **62.61 μs** |     **2.718 μs** |     **7.303 μs** |     **61.25 μs** |      **?** |       **?** |    **26.63 KB** |           **?** |
| User_FullMiss_CopyOnRead                 | 100       | 10                   |     67.76 μs |     5.211 μs |    14.264 μs |     63.50 μs |      ? |       ? |    26.63 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullMiss_Snapshot**                   | **100**       | **100**                  |    **243.24 μs** |    **12.174 μs** |    **32.912 μs** |    **249.60 μs** |      **?** |       **?** |   **209.86 KB** |           **?** |
| User_FullMiss_CopyOnRead                 | 100       | 100                  |    254.16 μs |     4.038 μs |     7.177 μs |    252.25 μs |      ? |       ? |   209.86 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullMiss_Snapshot**                   | **1000**      | **1**                    |     **69.86 μs** |     **2.952 μs** |     **7.828 μs** |     **69.75 μs** |      **?** |       **?** |    **30.07 KB** |           **?** |
| User_FullMiss_CopyOnRead                 | 1000      | 1                    |     70.67 μs |     2.214 μs |     5.948 μs |     69.55 μs |      ? |       ? |    30.07 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullMiss_Snapshot**                   | **1000**      | **10**                   |    **223.71 μs** |    **17.981 μs** |    **48.611 μs** |    **246.00 μs** |      **?** |       **?** |   **212.67 KB** |           **?** |
| User_FullMiss_CopyOnRead                 | 1000      | 10                   |    258.50 μs |     4.766 μs |    11.047 μs |    255.60 μs |      ? |       ? |   212.67 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullMiss_Snapshot**                   | **1000**      | **100**                  |  **2,048.49 μs** |   **148.508 μs** |   **391.230 μs** |  **2,170.60 μs** |      **?** |       **?** |  **1812.57 KB** |           **?** |
| User_FullMiss_CopyOnRead                 | 1000      | 100                  |  2,071.37 μs |   162.848 μs |   423.263 μs |  2,187.60 μs |      ? |       ? |  1812.57 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullMiss_Snapshot**                   | **10000**     | **1**                    |    **338.11 μs** |     **6.745 μs** |    **16.545 μs** |    **342.95 μs** |      **?** |       **?** |   **247.76 KB** |           **?** |
| User_FullMiss_CopyOnRead                 | 10000     | 1                    |    341.64 μs |     7.774 μs |    20.884 μs |    345.10 μs |      ? |       ? |   247.76 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullMiss_Snapshot**                   | **10000**     | **10**                   |  **2,105.68 μs** |   **151.099 μs** |   **400.692 μs** |  **2,235.30 μs** |      **?** |       **?** |  **1847.02 KB** |           **?** |
| User_FullMiss_CopyOnRead                 | 10000     | 10                   |  2,110.47 μs |   146.844 μs |   381.668 μs |  2,254.40 μs |      ? |       ? |  1847.02 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_FullMiss_Snapshot**                   | **10000**     | **100**                  | **10,537.49 μs** | **1,543.784 μs** | **4,303.452 μs** |  **8,193.50 μs** |      **?** |       **?** | **16047.32 KB** |           **?** |
| User_FullMiss_CopyOnRead                 | 10000     | 100                  | 12,561.95 μs | 1,894.852 μs | 5,282.089 μs | 10,489.10 μs |      ? |       ? | 16047.32 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_PartialHit_ForwardShift_Snapshot**    | **100**       | **1**                    |     **58.72 μs** |     **5.008 μs** |    **14.042 μs** |     **55.80 μs** |      **?** |       **?** |     **5.34 KB** |           **?** |
| User_PartialHit_ForwardShift_CopyOnRead  | 100       | 1                    |     76.70 μs |     9.082 μs |    26.779 μs |     64.45 μs |      ? |       ? |     5.34 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot   | 100       | 1                    |     52.41 μs |     2.378 μs |     6.306 μs |     51.30 μs |      ? |       ? |     5.28 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead | 100       | 1                    |     67.44 μs |     9.796 μs |    28.263 μs |     54.55 μs |      ? |       ? |     5.29 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_PartialHit_ForwardShift_Snapshot**    | **100**       | **10**                   |    **106.46 μs** |     **2.497 μs** |     **6.707 μs** |    **105.40 μs** |      **?** |       **?** |    **19.61 KB** |           **?** |
| User_PartialHit_ForwardShift_CopyOnRead  | 100       | 10                   |    137.94 μs |    11.584 μs |    31.317 μs |    127.10 μs |      ? |       ? |    19.62 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot   | 100       | 10                   |     84.91 μs |     2.562 μs |     6.703 μs |     83.80 μs |      ? |       ? |    19.55 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead | 100       | 10                   |    101.34 μs |     5.741 μs |    14.716 μs |     98.40 μs |      ? |       ? |    19.56 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_PartialHit_ForwardShift_Snapshot**    | **100**       | **100**                  |    **524.70 μs** |    **37.092 μs** |    **99.646 μs** |    **560.45 μs** |      **?** |       **?** |   **161.86 KB** |           **?** |
| User_PartialHit_ForwardShift_CopyOnRead  | 100       | 100                  |    756.21 μs |    22.660 μs |    57.677 μs |    760.10 μs |      ? |       ? |   161.87 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot   | 100       | 100                  |    403.43 μs |    12.364 μs |    33.638 μs |    405.50 μs |      ? |       ? |    161.8 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead | 100       | 100                  |    485.43 μs |    15.330 μs |    39.019 μs |    490.10 μs |      ? |       ? |   161.81 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_PartialHit_ForwardShift_Snapshot**    | **1000**      | **1**                    |    **127.79 μs** |     **3.147 μs** |     **8.454 μs** |    **125.55 μs** |      **?** |       **?** |     **26.5 KB** |           **?** |
| User_PartialHit_ForwardShift_CopyOnRead  | 1000      | 1                    |    154.75 μs |     3.086 μs |     7.570 μs |    154.00 μs |      ? |       ? |    26.51 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot   | 1000      | 1                    |    100.85 μs |     2.402 μs |     6.413 μs |    100.40 μs |      ? |       ? |    26.45 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead | 1000      | 1                    |    113.48 μs |     4.102 μs |    10.440 μs |    112.65 μs |      ? |       ? |    26.45 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_PartialHit_ForwardShift_Snapshot**    | **1000**      | **10**                   |    **723.19 μs** |    **14.291 μs** |    **36.634 μs** |    **724.40 μs** |      **?** |       **?** |   **167.48 KB** |           **?** |
| User_PartialHit_ForwardShift_CopyOnRead  | 1000      | 10                   |    755.95 μs |    33.956 μs |    90.045 μs |    773.85 μs |      ? |       ? |   167.49 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot   | 1000      | 10                   |    406.49 μs |     5.312 μs |    10.609 μs |    407.40 μs |      ? |       ? |   167.43 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead | 1000      | 10                   |    508.24 μs |     4.750 μs |    11.288 μs |    505.50 μs |      ? |       ? |   167.44 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_PartialHit_ForwardShift_Snapshot**    | **1000**      | **100**                  |  **6,129.94 μs** |   **385.340 μs** | **1,136.183 μs** |  **6,620.25 μs** |      **?** |       **?** |  **1575.21 KB** |           **?** |
| User_PartialHit_ForwardShift_CopyOnRead  | 1000      | 100                  |  6,446.39 μs |   419.097 μs | 1,202.469 μs |  6,850.55 μs |      ? |       ? |  1575.22 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot   | 1000      | 100                  |  4,377.79 μs |   282.570 μs |   828.730 μs |  4,685.00 μs |      ? |       ? |  1575.16 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead | 1000      | 100                  |  3,820.06 μs |   305.845 μs |   826.869 μs |  4,047.25 μs |      ? |       ? |  1575.16 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_PartialHit_ForwardShift_Snapshot**    | **10000**     | **1**                    |    **696.49 μs** |    **15.555 μs** |    **42.320 μs** |    **719.00 μs** |      **?** |       **?** |   **237.66 KB** |           **?** |
| User_PartialHit_ForwardShift_CopyOnRead  | 10000     | 1                    |    787.21 μs |    53.590 μs |   157.169 μs |    701.20 μs |      ? |       ? |   237.66 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot   | 10000     | 1                    |    778.11 μs |     5.062 μs |     8.174 μs |    778.05 μs |      ? |       ? |    237.6 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead | 10000     | 1                    |    811.02 μs |    46.978 μs |   138.516 μs |    742.15 μs |      ? |       ? |   237.61 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_PartialHit_ForwardShift_Snapshot**    | **10000**     | **10**                   |  **6,598.57 μs** |   **269.099 μs** |   **758.997 μs** |  **6,764.45 μs** |      **?** |       **?** |  **1644.12 KB** |           **?** |
| User_PartialHit_ForwardShift_CopyOnRead  | 10000     | 10                   |  6,963.86 μs |   326.050 μs |   881.496 μs |  7,310.30 μs |      ? |       ? |  1644.13 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot   | 10000     | 10                   |  3,315.61 μs |   310.699 μs |   802.013 μs |  3,697.05 μs |      ? |       ? |  1644.06 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead | 10000     | 10                   |  4,343.07 μs |   328.320 μs |   847.498 μs |  4,653.60 μs |      ? |       ? |  1644.07 KB |           ? |
|                                          |           |                      |              |              |              |              |        |         |             |             |
| **User_PartialHit_ForwardShift_Snapshot**    | **10000**     | **100**                  | **27,304.27 μs** | **1,686.910 μs** | **4,812.849 μs** | **25,289.10 μs** |      **?** |       **?** | **15708.09 KB** |           **?** |
| User_PartialHit_ForwardShift_CopyOnRead  | 10000     | 100                  | 36,889.53 μs | 2,344.198 μs | 6,911.922 μs | 35,258.20 μs |      ? |       ? | 15708.38 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot   | 10000     | 100                  | 21,344.69 μs | 1,804.776 μs | 5,235.982 μs | 19,536.40 μs |      ? |       ? | 15708.31 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead | 10000     | 100                  | 23,614.83 μs | 2,215.154 μs | 6,531.432 μs | 23,086.85 μs |      ? |       ? | 15708.32 KB |           ? |
