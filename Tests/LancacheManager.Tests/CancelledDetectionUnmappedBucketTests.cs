namespace LancacheManager.Tests;

/// <summary>
/// A detection run that stops early writes the games and services it reached, but it never
/// finished walking the cache, so it has no remainder of its own. Nothing else clears
/// <c>UnmappedServicesJson</c>: the summary refresh does not write that column, and the only clear
/// is <c>SaveUnmappedServicesAsync(null)</c>. Without that clear, the last full scan's remainder
/// stays on the singleton row beside the rows the stopped run just saved, and the panel presents
/// an older measurement as the current one.
///
/// Three exits end a run that way and they must agree: the user cancelled it, a linked timeout
/// fired, or it threw. Guarding one and not its siblings leaves the next reader unable to tell
/// which behavior was intended.
/// </summary>
public sealed class CancelledDetectionUnmappedBucketTests
{
    [Fact]
    public void EveryExitThatDidNotFinishTheWalk_ClearsTheStoredBucket()
    {
        var source = ReadSource("Core", "Services", "Detection", "GameCacheDetectionService.cs");

        foreach (var (exit, block) in UnfinishedRunExits(source))
        {
            Assert.True(
                block.Contains("ClearUnmappedTotalsAsync(", StringComparison.Ordinal),
                $"{exit} does not clear the stored unmapped totals");
        }
    }

    /// <summary>
    /// The three exits that end a run before it saved a bucket of its own, each scoped to its own
    /// block so a call belonging to an adjacent exit cannot satisfy another's assertion.
    /// </summary>
    private static IEnumerable<(string Exit, string Block)> UnfinishedRunExits(string source)
    {
        var cancelled = Marker(source, "catch (OperationCanceledException oce)", 0);
        var timedOut = Marker(source, "_logger.LogError(oce,", cancelled);
        var failed = Marker(source, "catch (Exception ex)", timedOut);
        var teardown = Marker(source, "finally", failed);

        yield return ("The cancellation handler", source[cancelled..timedOut]);
        yield return ("The timeout handler", source[timedOut..failed]);
        yield return ("The failure handler", source[failed..teardown]);
    }

    private static int Marker(string source, string marker, int searchFrom)
    {
        var at = source.IndexOf(marker, searchFrom, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{marker}' was not found after offset {searchFrom}; the run's exits were restructured");
        return at;
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
