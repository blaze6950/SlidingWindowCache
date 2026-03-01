using System.Collections;

namespace SlidingWindowCache.Infrastructure.Collections;

/// <summary>
/// A lightweight <see cref="IEnumerable{T}"/> wrapper over a <see cref="ReadOnlyMemory{T}"/>
/// that avoids allocating temp TData[] and copying the underlying data.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <remarks>
/// <para>
/// The <see cref="ReadOnlyMemory{T}"/> captured at construction keeps a reference to the
/// backing array, ensuring the data remains reachable for the lifetime of this enumerable.
/// </para>
/// <para>
/// Enumeration accesses elements via <c>ReadOnlyMemory&lt;T&gt;.Span</c> inside
/// <see cref="Enumerator.Current"/>, which is valid because the property is not an iterator
/// method and holds no state across yield boundaries.
/// </para>
/// </remarks>
internal sealed class ReadOnlyMemoryEnumerable<T> : IEnumerable<T>
{
    private readonly ReadOnlyMemory<T> _memory;

    /// <summary>
    /// Initializes a new <see cref="ReadOnlyMemoryEnumerable{T}"/> wrapping the given memory.
    /// </summary>
    /// <param name="memory">The memory region to enumerate.</param>
    public ReadOnlyMemoryEnumerable(ReadOnlyMemory<T> memory)
    {
        _memory = memory;
    }

    /// <summary>
    /// Returns an enumerator that iterates through the memory region.
    /// </summary>
    public Enumerator GetEnumerator() => new(_memory);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Enumerator(_memory);

    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(_memory);

    /// <summary>
    /// Enumerator for <see cref="ReadOnlyMemoryEnumerable{T}"/>.
    /// Accesses each element via index into <see cref="ReadOnlyMemory{T}.Span"/>.
    /// </summary>
    internal struct Enumerator : IEnumerator<T>
    {
        private readonly ReadOnlyMemory<T> _memory;
        private int _index;

        internal Enumerator(ReadOnlyMemory<T> memory)
        {
            _memory = memory;
            _index = -1;
        }

        /// <inheritdoc/>
        public T Current => _memory.Span[_index];

        object? IEnumerator.Current => Current;

        /// <inheritdoc/>
        public bool MoveNext() => ++_index < _memory.Length;

        /// <inheritdoc/>
        public void Reset() => _index = -1;

        /// <inheritdoc/>
        public void Dispose() { }
    }
}
