using BenchmarkDotNet.Running;

namespace Intervals.NET.Caching.Benchmarks;

/// <summary>
/// BenchmarkDotNet runner for Intervals.NET.Caching performance benchmarks.
/// Covers SlidingWindow (SWC), VisitedPlaces (VPC), and Layered cache implementations.
/// </summary>
public class Program
{
    public static void Main(string[] args)
    {
        // Run all benchmark classes via switcher (supports --filter)
        var summary = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
