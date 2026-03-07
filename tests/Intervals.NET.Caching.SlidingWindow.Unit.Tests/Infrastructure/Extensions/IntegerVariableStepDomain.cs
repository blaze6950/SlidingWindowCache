using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.SlidingWindow.Unit.Tests.Infrastructure.Extensions;

/// <summary>
/// Test implementation of IVariableStepDomain for integer values with custom step sizes.
/// Used for testing domain-agnostic extension methods with variable-step domains.
/// </summary>
public class IntegerVariableStepDomain : IVariableStepDomain<int>
{
    private readonly int[] _steps;

    public IntegerVariableStepDomain(int[] steps)
    {
        if (steps == null || steps.Length == 0)
        {
            throw new ArgumentException("Steps array cannot be null or empty.", nameof(steps));
        }

        // Ensure steps are sorted
        _steps = steps.OrderBy(s => s).ToArray();
    }

    public IComparer<int> Comparer => Comparer<int>.Default;

    public int? GetPreviousStep(int value)
    {
        for (var i = _steps.Length - 1; i >= 0; i--)
        {
            if (Comparer.Compare(_steps[i], value) < 0)
            {
                return _steps[i];
            }
        }
        return null;
    }

    public int? GetNextStep(int value)
    {
        foreach (var step in _steps)
        {
            if (Comparer.Compare(step, value) > 0)
            {
                return step;
            }
        }
        return null;
    }

    // IRangeDomain<int> base interface methods
    public int Add(int value, long steps)
    {
        if (steps == 0)
        {
            return value;
        }

        var current = value;
        if (steps > 0)
        {
            for (long i = 0; i < steps; i++)
            {
                var next = GetNextStep(current);
                if (next == null)
                {
                    throw new InvalidOperationException($"Cannot add {steps} steps from {value}: no more steps available");
                }

                current = next.Value;
            }
        }
        else
        {
            for (long i = 0; i < -steps; i++)
            {
                var prev = GetPreviousStep(current);
                if (prev == null)
                {
                    throw new InvalidOperationException($"Cannot subtract {-steps} steps from {value}: no more steps available");
                }

                current = prev.Value;
            }
        }
        return current;
    }

    public int Subtract(int value, long steps)
    {
        return Add(value, -steps);
    }

    public int Floor(int value)
    {
        // Find the largest step <= value
        for (var i = _steps.Length - 1; i >= 0; i--)
        {
            if (Comparer.Compare(_steps[i], value) <= 0)
            {
                return _steps[i];
            }
        }
        // If no step is <= value, return the first step
        return _steps[0];
    }

    public int Ceiling(int value)
    {
        // Find the smallest step >= value
        foreach (var step in _steps)
        {
            if (Comparer.Compare(step, value) >= 0)
            {
                return step;
            }
        }
        // If no step is >= value, return the last step
        return _steps[^1];
    }

    public long Distance(int from, int to)
    {
        var comparison = Comparer.Compare(from, to);
        if (comparison == 0)
        {
            return 0;
        }

        var start = comparison < 0 ? from : to;
        var end = comparison < 0 ? to : from;

        long count = 0;
        var current = start;

        while (Comparer.Compare(current, end) < 0)
        {
            var next = GetNextStep(current);
            if (next == null)
            {
                break;
            }

            current = next.Value;
            count++;
        }

        return comparison < 0 ? count : -count;
    }
}
