using SlidingWindowCache.Infrastructure.Instrumentation;

namespace SlidingWindowCache.Unit.Tests.Infrastructure.Instrumentation;

/// <summary>
/// Unit tests for NoOpDiagnostics to ensure it never throws exceptions.
/// This is critical because diagnostic failures should never break cache functionality.
/// </summary>
public class NoOpDiagnosticsTests
{
    [Fact]
    public void AllMethods_WhenCalled_DoNotThrowExceptions()
    {
        // ARRANGE
        var diagnostics = new NoOpDiagnostics();
        var testException = new InvalidOperationException("Test exception");

        // ACT & ASSERT - Call all methods and verify none throw exceptions
        var exception = Record.Exception(() =>
        {
            diagnostics.CacheExpanded();
            diagnostics.CacheReplaced();
            diagnostics.DataSourceFetchMissingSegments();
            diagnostics.DataSourceFetchSingleRange();
            diagnostics.RebalanceExecutionCancelled();
            diagnostics.RebalanceExecutionCompleted();
            diagnostics.RebalanceExecutionStarted();
            diagnostics.RebalanceIntentCancelled();
            diagnostics.RebalanceIntentPublished();
            diagnostics.RebalanceSkippedCurrentNoRebalanceRange();
            diagnostics.RebalanceSkippedPendingNoRebalanceRange();
            diagnostics.RebalanceSkippedSameRange();
            diagnostics.RebalanceExecutionFailed(testException);
            diagnostics.UserRequestFullCacheHit();
            diagnostics.UserRequestFullCacheMiss();
            diagnostics.UserRequestPartialCacheHit();
            diagnostics.UserRequestServed();
        });

        Assert.Null(exception);
    }
}
