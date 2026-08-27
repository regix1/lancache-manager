using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Tests;

/// <summary>
/// A killed child and a crashed one both exit non-zero, so the exit code alone cannot say which
/// happened. These pin the two halves of the classification: a cancelled run must surface as
/// cancellation, and a run that was never cancelled must still surface as a process failure.
/// </summary>
public class RustProcessCancellationClassificationTests
{
    [Fact]
    public void NonZeroExitAfterCancellationIsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = new ProcessExecutionResult
        {
            ExitCode = 137,
            Error = "killed"
        };

        Assert.Throws<OperationCanceledException>(
            () => result.EnsureSuccess("cache_cleaner", "default", cts.Token));
    }

    [Fact]
    public void NonZeroExitWithoutCancellationStaysAFailure()
    {
        using var cts = new CancellationTokenSource();

        var result = new ProcessExecutionResult
        {
            ExitCode = 1,
            Error = "cache path is unreadable"
        };

        var failure = Assert.Throws<RustProcessException>(
            () => result.EnsureSuccess("cache_cleaner", "default", cts.Token));

        Assert.Equal(1, failure.ExitCode);
        Assert.Equal("cache_cleaner", failure.Tool);
        Assert.Equal("cache path is unreadable", failure.Stderr);
    }
}
