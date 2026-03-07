namespace Intervals.NET.Caching.Infrastructure.Scheduling;

/// <summary>
/// Diagnostics callbacks for a work scheduler's execution lifecycle.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>
/// Provides the scheduler-level subset of diagnostics that
/// <see cref="WorkSchedulerBase{TWorkItem}"/> needs to report:
/// work started, cancelled, and failed.
/// This keeps the generic schedulers in <c>Intervals.NET.Caching</c>
/// fully decoupled from any cache-type-specific diagnostics interface
/// (e.g. <c>ICacheDiagnostics</c> in SlidingWindow).
/// </para>
/// <para><strong>Adapter Pattern:</strong></para>
/// <para>
/// Concrete cache implementations supply a thin adapter that bridges their own
/// diagnostics interface to <see cref="IWorkSchedulerDiagnostics"/>. For SlidingWindow
/// this adapter is <c>SlidingWindowWorkSchedulerDiagnostics</c>, which delegates to
/// <c>ICacheDiagnostics.RebalanceExecution*</c> methods.
/// </para>
/// <para><strong>Thread Safety:</strong></para>
/// <para>
/// All methods must be safe to call concurrently from background threads.
/// Implementations must not throw.
/// </para>
/// </remarks>
internal interface IWorkSchedulerDiagnostics
{
    /// <summary>
    /// Called at the start of executing a work item, before the debounce delay.
    /// </summary>
    void WorkStarted();

    /// <summary>
    /// Called when a work item is cancelled (via <see cref="OperationCanceledException"/>
    /// or a post-debounce <see cref="CancellationToken.IsCancellationRequested"/> check).
    /// </summary>
    void WorkCancelled();

    /// <summary>
    /// Called when a work item fails with an unhandled exception.
    /// </summary>
    /// <param name="ex">The exception that caused the failure.</param>
    void WorkFailed(Exception ex);
}
