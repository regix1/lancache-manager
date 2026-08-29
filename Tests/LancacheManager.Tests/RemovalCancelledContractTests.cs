using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// A stopped run must reach the UI as a cancellation, not as a failure.
///
/// The completion payloads default <c>Cancelled</c> to false, so a cancel path that omits the
/// argument still compiles and still sends <c>Success: false</c>. The browser cannot tell that
/// apart from a genuine error: it paints the card red and announces it as an alert, even though
/// the message it shows says the run was cancelled. Both removal controllers shipped with that
/// omission while every other cancel emitter passed the flag.
/// </summary>
public sealed class RemovalCancelledContractTests
{
    [Fact]
    public void GameRemovalCancelledPayload_ReportsCancelled()
    {
        var block = CancelledPayloadBlock(
            ReadSource("Controllers", "Cache", "GamesController.cs"));

        Assert.Contains("Cancelled: true", block);
    }

    [Fact]
    public void ServiceRemovalCancelledPayload_ReportsCancelled()
    {
        var block = CancelledPayloadBlock(
            ReadSource("Controllers", "Cache", "CacheController.cs"));

        Assert.Contains("Cancelled: true", block);
    }

    [Fact]
    public void OmittingTheFlag_MakesACancelledRunLookLikeAFailure()
    {
        // Why the two assertions above matter: the only difference between the two payloads
        // below is the argument, and it is what decides the status the frontend reads.
        var withoutFlag = new SignalRNotifications.GameRemovalComplete(
            Success: false,
            OperationId: Guid.NewGuid(),
            GameAppId: 730,
            EpicAppId: null,
            StageKey: "signalr.gameRemove.cancelled");

        var withFlag = new SignalRNotifications.GameRemovalComplete(
            Success: false,
            OperationId: Guid.NewGuid(),
            GameAppId: 730,
            EpicAppId: null,
            StageKey: "signalr.gameRemove.cancelled",
            Cancelled: true);

        Assert.Equal(OperationStatus.Failed, ((IOperationComplete)withoutFlag).Status);
        Assert.Equal(OperationStatus.Cancelled, ((IOperationComplete)withFlag).Status);
    }

    /// <summary>
    /// The <c>BuildCancelledPayload</c> argument, from its name up to the next builder argument.
    /// Scoped so a <c>Cancelled: true</c> belonging to a different payload in the same file cannot
    /// satisfy the assertion.
    /// </summary>
    private static string CancelledPayloadBlock(string source)
    {
        var start = source.IndexOf("BuildCancelledPayload", StringComparison.Ordinal);
        Assert.True(start >= 0, "BuildCancelledPayload not found; the cancel path was renamed or removed");

        var next = source.IndexOf("BuildErrorProgressPayload", start, StringComparison.Ordinal);
        Assert.True(next > start, "BuildCancelledPayload is no longer followed by BuildErrorProgressPayload");

        return source[start..next];
    }

    private static string ReadSource(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        return File.ReadAllText(Path.Combine([root, "Api", "LancacheManager", .. pathSegments]));
    }
}
