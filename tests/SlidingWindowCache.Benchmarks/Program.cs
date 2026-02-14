using BenchmarkDotNet.Running;

namespace SlidingWindowCache.Benchmarks;

/// <summary>
/// BenchmarkDotNet runner for SlidingWindowCache performance benchmarks.
/// </summary>
public class Program
{
    public static void Main(string[] args)
    {
        // Run all benchmark classes
        var summary = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

        // Alternative: Run specific benchmark
        // var summary = BenchmarkRunner.Run<ReadPerformanceBenchmarks>();
    }
}