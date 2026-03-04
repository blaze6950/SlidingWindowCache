namespace SlidingWindowCache.Core.State;

/// <summary>
/// Thread-safe holder for the current <see cref="RuntimeCacheOptions"/> snapshot.
/// Supports atomic, lock-free reads and writes using <see cref="Volatile"/> memory barriers.
/// </summary>
/// <remarks>
/// <para><strong>Architectural Context:</strong></para>
/// <para>
/// <see cref="RuntimeCacheOptionsHolder"/> is the shared configuration bridge between the user thread
/// (which calls <c>IWindowCache.UpdateRuntimeOptions</c>) and the background threads (intent loop,
/// execution controllers) that read the current options during decision and execution.
/// </para>
/// <para><strong>Memory Model:</strong></para>
/// <list type="bullet">
/// <item><description><strong>Write (user thread):</strong> <see cref="Update"/> uses <see cref="Volatile.Write"/> (release fence) — ensures the fully-constructed new snapshot is visible to all subsequent reads.</description></item>
/// <item><description><strong>Read (background threads):</strong> <see cref="Current"/> uses <see cref="Volatile.Read"/> (acquire fence) — ensures reads observe the latest published snapshot.</description></item>
/// </list>
/// <para><strong>Consistency Guarantee:</strong></para>
/// <para>
/// Because the entire <see cref="RuntimeCacheOptions"/> reference is swapped atomically, background threads
/// always observe a consistent set of all five values. There is never a partial-update window.
/// Updates take effect on the next background read cycle ("next cycle" semantics), which is compatible
/// with the system's eventual consistency model.
/// </para>
/// <para><strong>Concurrent Updates:</strong></para>
/// <para>
/// Multiple concurrent calls to <see cref="Update"/> are safe: last-writer-wins. This is acceptable
/// for configuration updates where the latest user intent should always prevail.
/// </para>
/// </remarks>
internal sealed class RuntimeCacheOptionsHolder
{
    // The currently active configuration snapshot.
    // Written via Volatile.Write (release fence); read via Volatile.Read (acquire fence).
    private RuntimeCacheOptions _current;

    /// <summary>
    /// Initializes a new <see cref="RuntimeCacheOptionsHolder"/> with the provided initial snapshot.
    /// </summary>
    /// <param name="initial">The initial runtime options snapshot. Must not be <c>null</c>.</param>
    public RuntimeCacheOptionsHolder(RuntimeCacheOptions initial)
    {
        _current = initial;
    }

    /// <summary>
    /// Returns the currently active <see cref="RuntimeCacheOptions"/> snapshot.
    /// Uses <see cref="Volatile.Read"/> to ensure the freshest published snapshot is observed.
    /// </summary>
    /// <remarks>
    /// Callers should snapshot this value at the start of a decision/execution unit of work
    /// and use that snapshot consistently throughout, rather than calling this property multiple times.
    /// </remarks>
    public RuntimeCacheOptions Current => Volatile.Read(ref _current);

    /// <summary>
    /// Atomically replaces the current snapshot with <paramref name="newOptions"/>.
    /// Uses <see cref="Volatile.Write"/> to publish the new reference with a release fence,
    /// ensuring it is immediately visible to all subsequent <see cref="Current"/> reads.
    /// </summary>
    /// <param name="newOptions">The new options snapshot. Must not be <c>null</c>.</param>
    public void Update(RuntimeCacheOptions newOptions)
    {
        Volatile.Write(ref _current, newOptions);
    }
}
