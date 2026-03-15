```

BenchmarkDotNet v0.13.12, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core i7-1065G7 CPU 1.30GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 8.0.418
  [Host]     : .NET 8.0.24 (8.0.2426.7010), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-BUXWGJ : .NET 8.0.24 (8.0.2426.7010), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

InvocationCount=1  UnrollFactor=1  

```
| Method                                    | RangeSpan | CacheCoefficientSize |             Mean |            Error |           StdDev |           Median |    Ratio |  RatioSD |       Allocated | Alloc Ratio |
|-------------------------------------------|-----------|----------------------|-----------------:|-----------------:|-----------------:|-----------------:|---------:|---------:|----------------:|------------:|
| **User_FullHit_Snapshot**                 | **100**   | **1**                |     **29.96 μs** |     **2.855 μs** |     **7.960 μs** |     **30.85 μs** | **1.00** | **0.00** |     **1.38 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 100       | 1                    |         35.13 μs |         4.092 μs |        11.806 μs |         30.50 μs |     1.21 |     0.33 |         2.12 KB |        1.54 |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **100**   | **10**               |     **30.85 μs** |     **2.636 μs** |     **7.604 μs** |     **31.90 μs** | **1.00** | **0.00** |     **1.38 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 100       | 10                   |         48.88 μs |         8.043 μs |        23.462 μs |         49.75 μs |     1.54 |     0.44 |         6.38 KB |        4.64 |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **100**   | **100**              |     **27.20 μs** |     **2.017 μs** |     **5.688 μs** |     **24.45 μs** | **1.00** | **0.00** |     **1.38 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 100       | 100                  |         69.98 μs |         7.059 μs |        20.703 μs |         78.00 μs |     2.62 |     0.56 |        48.98 KB |       35.62 |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **1000**  | **1**                |     **29.70 μs** |     **2.644 μs** |     **7.457 μs** |     **26.55 μs** | **1.00** | **0.00** |     **1.38 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 1000      | 1                    |         49.76 μs |         8.004 μs |        23.221 μs |         56.40 μs |     1.69 |     0.64 |         8.45 KB |        6.14 |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **1000**  | **10**               |     **26.67 μs** |     **2.065 μs** |     **5.892 μs** |     **24.05 μs** | **1.00** | **0.00** |     **1.38 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 1000      | 10                   |         71.54 μs |         7.724 μs |        22.409 μs |         78.70 μs |     2.72 |     0.74 |        50.67 KB |       36.85 |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **1000**  | **100**              |     **24.30 μs** |     **2.301 μs** |     **6.376 μs** |     **21.60 μs** | **1.00** | **0.00** |     **1.38 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 1000      | 100                  |        302.58 μs |        10.121 μs |        29.524 μs |        296.35 μs |    13.47 |     4.45 |       472.97 KB |      343.98 |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **10000** | **1**                |     **27.95 μs** |     **2.182 μs** |     **6.153 μs** |     **29.05 μs** | **1.00** | **0.00** |     **1.38 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 10000     | 1                    |         85.71 μs |         7.473 μs |        21.916 μs |         92.50 μs |     3.13 |     0.48 |        71.73 KB |       52.16 |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **10000** | **10**               |     **27.82 μs** |     **2.442 μs** |     **6.766 μs** |     **28.00 μs** | **1.00** | **0.00** |     **1.38 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 10000     | 10                   |        315.29 μs |        12.731 μs |        37.337 μs |        309.20 μs |    12.04 |     2.90 |       493.64 KB |      359.01 |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullHit_Snapshot**                 | **10000** | **100**              |     **14.01 μs** |     **1.748 μs** |     **4.786 μs** |     **12.80 μs** | **1.00** | **0.00** |     **1.38 KB** |    **1.00** |
| User_FullHit_CopyOnRead                   | 10000     | 100                  |      1,880.60 μs |       257.551 μs |       755.351 μs |      2,162.30 μs |   143.58 |    48.53 |      4712.81 KB |    3,427.50 |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **100**   | **1**                |     **44.32 μs** |     **3.037 μs** |     **8.364 μs** |     **43.05 μs** |    **?** |    **?** |     **8.43 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 100       | 1                    |         43.19 μs |         3.200 μs |         8.973 μs |         41.50 μs |        ? |        ? |         8.43 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **100**   | **10**               |     **65.40 μs** |     **2.306 μs** |     **6.390 μs** |     **64.40 μs** |    **?** |    **?** |     **43.6 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 100       | 10                   |         64.70 μs |         2.707 μs |         7.501 μs |         63.80 μs |        ? |        ? |         43.6 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **100**   | **100**              |    **237.37 μs** |    **10.835 μs** |    **29.477 μs** |    **242.55 μs** |    **?** |    **?** |   **338.69 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 100       | 100                  |        230.09 μs |        14.281 μs |        38.851 μs |        241.45 μs |        ? |        ? |       338.69 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **1000**  | **1**                |     **73.20 μs** |     **3.111 μs** |     **8.463 μs** |     **72.35 μs** |    **?** |    **?** |    **46.08 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 1000      | 1                    |         70.86 μs |         2.302 μs |         6.183 μs |         69.80 μs |        ? |        ? |        47.05 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **1000**  | **10**               |    **254.12 μs** |     **7.715 μs** |    **20.989 μs** |    **255.85 μs** |    **?** |    **?** |    **341.5 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 1000      | 10                   |        255.75 μs |         5.140 μs |        14.665 μs |        254.85 μs |        ? |        ? |        341.5 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **1000**  | **100**              |  **2,029.39 μs** |   **161.830 μs** |   **474.619 μs** |  **2,207.40 μs** |    **?** |    **?** |   **2837.4 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 1000      | 100                  |      1,836.24 μs |       194.372 μs |       573.110 μs |      2,164.00 μs |        ? |        ? |      2836.02 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **10000** | **1**                |    **337.32 μs** |     **6.736 μs** |     **9.661 μs** |    **336.00 μs** |    **?** |    **?** |   **375.09 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 10000     | 1                    |        321.29 μs |         7.587 μs |        20.513 μs |        322.90 μs |        ? |        ? |       376.59 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **10000** | **10**               |  **2,674.83 μs** |   **211.148 μs** |   **622.575 μs** |  **2,802.20 μs** |    **?** |    **?** |  **2871.85 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 10000     | 10                   |      1,913.67 μs |       155.929 μs |       459.761 μs |      2,130.10 μs |        ? |        ? |      2871.85 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_FullMiss_Snapshot**                | **10000** | **100**              |  **7,949.13 μs** |   **155.932 μs** |   **292.877 μs** |  **7,905.60 μs** |    **?** |    **?** | **24238.63 KB** |       **?** |
| User_FullMiss_CopyOnRead                  | 10000     | 100                  |     10,734.45 μs |     1,270.301 μs |     3,725.574 μs |      8,346.10 μs |        ? |        ? |     24238.63 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **100**   | **1**                |     **62.20 μs** |     **3.479 μs** |     **9.164 μs** |     **61.70 μs** |    **?** |    **?** |     **7.55 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 100       | 1                    |         73.25 μs |         8.521 μs |        24.720 μs |         61.85 μs |        ? |        ? |         8.63 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 100       | 1                    |         60.92 μs |         2.312 μs |         5.969 μs |         60.25 μs |        ? |        ? |         8.57 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 100       | 1                    |         67.06 μs |         7.733 μs |        22.061 μs |         57.15 μs |        ? |        ? |         8.58 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **100**   | **10**               |    **131.90 μs** |     **5.349 μs** |    **14.186 μs** |    **133.30 μs** |    **?** |    **?** |    **36.97 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 100       | 10                   |        104.56 μs |         3.975 μs |        10.540 μs |        102.80 μs |        ? |        ? |        36.98 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 100       | 10                   |        102.07 μs |         3.674 μs |         9.995 μs |        101.60 μs |        ? |        ? |        36.91 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 100       | 10                   |         98.00 μs |         7.240 μs |        18.818 μs |         93.70 μs |        ? |        ? |        36.92 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **100**   | **100**              |    **652.47 μs** |    **23.683 μs** |    **64.028 μs** |    **664.40 μs** |    **?** |    **?** |    **289.8 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 100       | 100                  |        485.86 μs |        26.372 μs |        68.076 μs |        502.25 μs |        ? |        ? |        289.8 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 100       | 100                  |        465.19 μs |        22.154 μs |        59.134 μs |        476.15 μs |        ? |        ? |       291.23 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 100       | 100                  |        389.69 μs |        27.684 μs |        71.954 μs |        416.40 μs |        ? |        ? |       289.75 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **1000**  | **1**                |    **155.32 μs** |     **3.576 μs** |     **9.544 μs** |    **155.70 μs** |    **?** |    **?** |    **43.86 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 1000      | 1                    |        124.29 μs |         4.768 μs |        12.309 μs |        123.35 μs |        ? |        ? |        43.87 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 1000      | 1                    |        123.71 μs |         2.206 μs |         4.796 μs |        123.80 μs |        ? |        ? |         43.8 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 1000      | 1                    |        105.33 μs |         4.644 μs |        12.153 μs |        106.50 μs |        ? |        ? |        43.81 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **1000**  | **10**               |    **670.66 μs** |    **24.535 μs** |    **65.910 μs** |    **681.60 μs** |    **?** |    **?** |   **296.91 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 1000      | 10                   |        514.15 μs |        10.155 μs |        25.664 μs |        517.50 μs |        ? |        ? |       296.92 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 1000      | 10                   |        621.96 μs |        14.831 μs |        42.313 μs |        626.95 μs |        ? |        ? |       296.86 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 1000      | 10                   |        489.42 μs |        31.658 μs |        92.348 μs |        448.95 μs |        ? |        ? |        295.6 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **1000**  | **100**              |  **5,248.27 μs** |   **510.892 μs** | **1,506.376 μs** |  **5,894.90 μs** |    **?** |    **?** |  **2600.71 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 1000      | 100                  |      4,767.05 μs |       409.194 μs |     1,193.638 μs |      5,281.85 μs |        ? |        ? |      2600.72 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 1000      | 100                  |      3,755.66 μs |       343.639 μs |       957.927 μs |      4,144.60 μs |        ? |        ? |      2599.16 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 1000      | 100                  |      3,228.39 μs |       296.816 μs |       797.378 μs |      3,632.55 μs |        ? |        ? |      2600.66 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **10000** | **1**                |  **1,016.99 μs** |     **6.934 μs** |    **12.853 μs** |  **1,014.90 μs** |    **?** |    **?** |   **365.59 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 10000     | 1                    |        713.44 μs |        14.272 μs |        36.842 μs |        714.55 μs |        ? |        ? |       367.09 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 10000     | 1                    |        732.28 μs |        26.092 μs |        70.095 μs |        710.90 μs |        ? |        ? |       367.03 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 10000     | 1                    |        573.70 μs |        11.410 μs |        27.556 μs |        578.80 μs |        ? |        ? |       367.04 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **10000** | **10**               |  **5,623.62 μs** |   **409.161 μs** | **1,133.784 μs** |  **6,097.60 μs** |    **?** |    **?** |  **2669.62 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 10000     | 10                   |      5,195.34 μs |       373.495 μs |     1,083.577 μs |      5,588.80 μs |        ? |        ? |      2668.13 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 10000     | 10                   |      4,019.55 μs |       327.104 μs |       900.940 μs |      4,382.55 μs |        ? |        ? |      2668.16 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 10000     | 10                   |      3,449.88 μs |       301.895 μs |       779.287 μs |      3,779.80 μs |        ? |        ? |      2669.57 KB |           ? |
|                                           |           |                      |                  |                  |                  |                  |          |          |                 |             |
| **User_PartialHit_ForwardShift_Snapshot** | **10000** | **100**              | **29,005.11 μs** | **1,309.680 μs** | **3,861.622 μs** | **27,406.10 μs** |    **?** |    **?** | **23900.88 KB** |       **?** |
| User_PartialHit_ForwardShift_CopyOnRead   | 10000     | 100                  |     23,645.77 μs |     1,477.890 μs |     4,311.074 μs |     21,620.00 μs |        ? |        ? |      23901.2 KB |           ? |
| User_PartialHit_BackwardShift_Snapshot    | 10000     | 100                  |     20,928.49 μs |     1,412.896 μs |     4,165.956 μs |     18,886.40 μs |        ? |        ? |     23900.39 KB |           ? |
| User_PartialHit_BackwardShift_CopyOnRead  | 10000     | 100                  |     18,722.83 μs |     1,429.961 μs |     4,193.828 μs |     16,507.45 μs |        ? |        ? |     23900.84 KB |           ? |
