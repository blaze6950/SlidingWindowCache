using BenchmarkDotNet.Running;

namespace Intervals.NET.Caching.Benchmarks;

/// <summary>
/// BenchmarkDotNet runner for Intervals.NET.Caching performance benchmarks.
/// </summary>
public class Program
{
    public static void Main(string[] args)
    {
        // Run all benchmark classes
        var summary = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

        // Alternative: Run specific benchmark
        // var summary = BenchmarkRunner.Run<RebalanceFlowBenchmarks>();
    }
}