using System.Reflection;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// After its scan, a Game Detection run names the unknown games it found, and that step stops on its
/// own after 60 seconds when the database does not answer. The run then ends failed, and the card
/// names that reason with a key both shipped languages translate, never the generic "Operation
/// failed", which drops the reason. The run is the shipped detection service on the detection race
/// tests' harness (GameDetectionVisibilityRaceTests.cs:455, private there, so it is reached by
/// reflection); the database the naming step opens never answers, and the step's own timer is made
/// to fire at once instead of after 60 seconds.
/// </summary>
public sealed class GameDetectionResolveTimeoutKeyTests
{
    private const string GenericFailureKey = "signalr.generic.failed";

    [Fact]
    public async Task ARunWhoseNamingStepTimesOut_EndsOnATranslatedReasonAsync()
    {
        var harness = typeof(GameDetectionVisibilityRaceTests).GetNestedType("DetectionRun", BindingFlags.NonPublic)!;
        var creating = (Task)harness.GetMethod("CreateAsync", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        await creating;
        var run = (IAsyncDisposable)creating.GetType().GetProperty("Result")!.GetValue(creating)!;
        await using var disposing = run;
        var detection = (GameCacheDetectionService)harness.GetProperty("Detection")!.GetValue(run)!;
        typeof(GameCacheDetectionService)
            .GetField("_unknownGameResolutionService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(detection, new UnknownGameResolutionService(new StalledDatabase(), NullLogger<UnknownGameResolutionService>.Instance));

        var id = (await detection.StartDetectionAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual), incremental: false))!.Value;
        var waiting = (Task<OperationInfo>)harness.GetMethod("WaitForTerminalAsync")!.Invoke(run, [id])!;
        var ended = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(OperationStatus.Failed, ended.Status);
        // The ending key the card and its notification translate (GameCacheDetectionService.cs:1154).
        var shown = Assert.IsType<GameDetectionMetrics>(ended.Metadata).CompletionStageKey;
        Assert.True(
            shown != GenericFailureKey && shown is { } key && LocaleKeys.TranslatedInBothLocales(key),
            $"a Game Detection run whose naming step timed out ends on {shown} (detail \"{ended.Message}\"), so the card says only \"Operation failed\" and drops why the run stopped");
    }

    /// <summary>
    /// A database that never answers the naming step. The step's own 60-second timer is the only thing
    /// that can end the wait, so the wait fires it at once, as it would fire after 60 seconds.
    /// </summary>
    private sealed class StalledDatabase : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => throw new InvalidOperationException("The naming step opens its database asynchronously.");

        public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            var timer = (CancellationTokenSource?)typeof(CancellationToken)
                .GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(cancellationToken);
            Assert.True(timer is not null, "the naming step opened its database without its timeout, so the test proves nothing");
            timer!.Cancel();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("A canceled wait never ends normally.");
        }
    }
}
