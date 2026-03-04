using Intervals.NET;
using Intervals.NET.Domain.Abstractions;

namespace Intervals.NET.Caching.Infrastructure.Extensions;

/// <summary>
/// Provides domain-agnostic extension methods that work with any IRangeDomain type.
/// These methods dispatch to the appropriate Fixed or Variable extension methods based on the runtime domain type.
/// </summary>
/// <remarks>
/// <para>
/// While Intervals.NET separates fixed-step and variable-step extension methods into different namespaces
/// to enforce explicit performance semantics at the API level, cache scenarios benefit from flexibility:
/// in-memory O(N) step counting (microseconds) is negligible compared to data source I/O (milliseconds to seconds).
/// </para>
/// <para>
/// These extensions enable the cache to work with any domain type, whether fixed-step or variable-step,
/// by dispatching to the appropriate implementation at runtime.
/// </para>
/// </remarks>
internal static class IntervalsNetDomainExtensions
{
    /// <summary>
    /// Calculates the number of discrete steps within a range for any domain type.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries.</typeparam>
    /// <typeparam name="TDomain">The domain type (can be fixed or variable-step).</typeparam>
    /// <param name="range">The range to measure.</param>
    /// <param name="domain">The domain defining discrete steps.</param>
    /// <returns>The number of discrete steps, or infinity if unbounded.</returns>
    /// <remarks>
    /// Performance: O(1) for fixed-step domains, O(N) for variable-step domains.
    /// The O(N) cost is acceptable because it represents in-memory computation that is orders of magnitude
    /// faster than data source I/O operations.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// Thrown when the domain does not implement either IFixedStepDomain or IVariableStepDomain.
    /// </exception>
    internal static RangeValue<long> Span<TRange, TDomain>(this Range<TRange> range, TDomain domain)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange> => domain switch
        {
            // FQN required: Intervals.NET exposes Span/Expand in separate Fixed and Variable namespaces
            // (Intervals.NET.Domain.Extensions.Fixed and ...Variable). Both namespaces define a
            // RangeDomainExtensions class with the same method names, so a using directive would cause
            // an ambiguity error. Full qualification unambiguously selects the correct overload at
            // compile time without polluting the file's namespace imports.
            IFixedStepDomain<TRange> fixedDomain => Intervals.NET.Domain.Extensions.Fixed.RangeDomainExtensions.Span(range, fixedDomain),
            IVariableStepDomain<TRange> variableDomain => Intervals.NET.Domain.Extensions.Variable.RangeDomainExtensions.Span(range, variableDomain),
            _ => throw new NotSupportedException(
                $"Domain type {domain.GetType().Name} must implement either IFixedStepDomain<T> or IVariableStepDomain<T>.")
        };

    /// <summary>
    /// Expands a range by a specified number of steps on each side for any domain type.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries.</typeparam>
    /// <typeparam name="TDomain">The domain type (can be fixed or variable-step).</typeparam>
    /// <param name="range">The range to expand.</param>
    /// <param name="domain">The domain defining discrete steps.</param>
    /// <param name="left">Number of steps to expand on the left.</param>
    /// <param name="right">Number of steps to expand on the right.</param>
    /// <returns>The expanded range.</returns>
    /// <remarks>
    /// Performance: O(1) for fixed-step domains, O(N) for variable-step domains.
    /// The O(N) cost is acceptable because it represents in-memory computation that is orders of magnitude
    /// faster than data source I/O operations.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// Thrown when the domain does not implement either IFixedStepDomain or IVariableStepDomain.
    /// </exception>
    internal static Range<TRange> Expand<TRange, TDomain>(
        this Range<TRange> range,
        TDomain domain,
        long left,
        long right)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange> => domain switch
        {
            IFixedStepDomain<TRange> fixedDomain => Intervals.NET.Domain.Extensions.Fixed.RangeDomainExtensions.Expand(
                range, fixedDomain, left, right),
            IVariableStepDomain<TRange> variableDomain => Intervals.NET.Domain.Extensions.Variable.RangeDomainExtensions
                .Expand(range, variableDomain, left, right),
            _ => throw new NotSupportedException(
                $"Domain type {domain.GetType().Name} must implement either IFixedStepDomain<T> or IVariableStepDomain<T>.")
        };

    /// <summary>
    /// Expands or shrinks a range by a ratio of its size for any domain type.
    /// </summary>
    /// <typeparam name="TRange">The type representing range boundaries.</typeparam>
    /// <typeparam name="TDomain">The domain type (can be fixed or variable-step).</typeparam>
    /// <param name="range">The range to modify.</param>
    /// <param name="domain">The domain defining discrete steps.</param>
    /// <param name="leftRatio">Ratio to expand/shrink the left boundary (negative shrinks).</param>
    /// <param name="rightRatio">Ratio to expand/shrink the right boundary (negative shrinks).</param>
    /// <returns>The modified range.</returns>
    /// <remarks>
    /// Performance: O(1) for fixed-step domains, O(N) for variable-step domains.
    /// The O(N) cost is acceptable because it represents in-memory computation that is orders of magnitude
    /// faster than data source I/O operations.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// Thrown when the domain does not implement either IFixedStepDomain or IVariableStepDomain.
    /// </exception>
    internal static Range<TRange> ExpandByRatio<TRange, TDomain>(
        this Range<TRange> range,
        TDomain domain,
        double leftRatio,
        double rightRatio)
        where TRange : IComparable<TRange>
        where TDomain : IRangeDomain<TRange> => domain switch
        {
            IFixedStepDomain<TRange> fixedDomain => Intervals.NET.Domain.Extensions.Fixed.RangeDomainExtensions
                .ExpandByRatio(range, fixedDomain, leftRatio, rightRatio),
            IVariableStepDomain<TRange> variableDomain => Intervals.NET.Domain.Extensions.Variable.RangeDomainExtensions
                .ExpandByRatio(range, variableDomain, leftRatio, rightRatio),
            _ => throw new NotSupportedException(
                $"Domain type {domain.GetType().Name} must implement either IFixedStepDomain<T> or IVariableStepDomain<T>.")
        };
}