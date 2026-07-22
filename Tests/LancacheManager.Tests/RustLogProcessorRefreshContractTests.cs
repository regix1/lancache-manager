using LancacheManager.Infrastructure.Services;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the committed-boundary DownloadsRefresh contract: the refresh fires only when a
/// valid terminal checkpoint proves committed rows, it precedes every post-pass, the
/// auto-tag pass emits its own conditional refresh, the trailing silent refresh stays
/// removed, and every DownloadsRefresh emission awaits NotifyAllAsync (cache-generation
/// bump before hub send), never fire-and-forget.
/// </summary>
public sealed class RustLogProcessorRefreshContractTests
{
    private static RustLogProcessorService.ProgressData TerminalCheckpoint(
        string terminalStatus, long entriesSaved) => new()
    {
        SchemaVersion = 1,
        TerminalStatus = terminalStatus,
        EntriesSaved = entriesSaved
    };

    [Theory]
    [InlineData("completed")]
    [InlineData("completed_with_warnings")]
    [InlineData("partial")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public void HasCommittedDownloads_TrueForEveryTerminalStatusWithSavedEntries(string status)
    {
        Assert.True(RustLogProcessorService.HasCommittedDownloads(TerminalCheckpoint(status, 1)));
    }

    [Fact]
    public void HasCommittedDownloads_FalseWhenNoEntriesWereSaved()
    {
        Assert.False(RustLogProcessorService.HasCommittedDownloads(TerminalCheckpoint("completed", 0)));
    }

    [Fact]
    public void HasCommittedDownloads_FalseWithoutValidTerminalCheckpoint()
    {
        Assert.False(RustLogProcessorService.HasCommittedDownloads(null));

        // A pre-contract writer (schema 0) or a non-terminal/unknown status must never be
        // trusted, even when it claims saved entries.
        Assert.False(RustLogProcessorService.HasCommittedDownloads(new RustLogProcessorService.ProgressData
        {
            SchemaVersion = 0,
            TerminalStatus = "completed",
            EntriesSaved = 10
        }));
        Assert.False(RustLogProcessorService.HasCommittedDownloads(TerminalCheckpoint("", 10)));
        Assert.False(RustLogProcessorService.HasCommittedDownloads(TerminalCheckpoint("done", 10)));
    }

    [Fact]
    public void CommittedRefreshPrecedesEveryPostPass()
    {
        var source = ReadSource("Infrastructure", "Services", "RustLogProcessorService.cs");

        var committedEmit = source.IndexOf("await NotifyCommittedDownloadsAsync(finalProgress);", StringComparison.Ordinal);
        var autoTag = source.IndexOf("await AutoTagNewDownloadsAsync();", StringComparison.Ordinal);
        var epicResolve = source.IndexOf("epicMappingService.ResolveDownloadsAsync()", StringComparison.Ordinal);
        var blizzardResolve = source.IndexOf("battleNetMappingService.ResolveDownloadsAsync()", StringComparison.Ordinal);
        var xboxResolve = source.IndexOf("xboxMappingService.ResolveDownloadsAsync()", StringComparison.Ordinal);

        Assert.True(committedEmit >= 0, "committed-boundary emit call site is missing");
        Assert.True(autoTag > committedEmit, "auto-tag must run after the committed-boundary refresh");
        Assert.True(epicResolve > autoTag, "Epic resolve must run after auto-tag");
        Assert.True(blizzardResolve > epicResolve, "Blizzard resolve must run after Epic");
        Assert.True(xboxResolve > blizzardResolve, "Xbox resolve must run after Blizzard");
    }

    [Fact]
    public void AutoTagReturnsCountAndEmitsConditionalRefresh()
    {
        var source = ReadSource("Infrastructure", "Services", "RustLogProcessorService.cs");

        var methodStart = source.IndexOf("private async Task<int> AutoTagNewDownloadsAsync()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "AutoTagNewDownloadsAsync must return the tagged count");

        var body = source[methodStart..];
        var conditional = body.IndexOf("taggedCount > 0", StringComparison.Ordinal);
        var refresh = body.IndexOf("NotifyAllAsync(SignalREvents.DownloadsRefresh", StringComparison.Ordinal);
        Assert.True(conditional >= 0, "auto-tag refresh must be gated on a positive tag count");
        Assert.True(refresh > conditional, "auto-tag refresh must sit inside the positive-count branch");
    }

    [Fact]
    public void ProcessorEmitsDownloadsRefreshOnlyViaAwaitedNotifyAllAsync()
    {
        var source = ReadSource("Infrastructure", "Services", "RustLogProcessorService.cs");

        // Fire-and-forget skips the dashboard cache-generation bump that must precede the
        // hub send, so it can serve a refetch a stale batch. It must never appear here.
        Assert.DoesNotContain("NotifyAllFireAndForget", source, StringComparison.Ordinal);

        // Exactly two emission sites: the committed-boundary helper and the auto-tag pass.
        // A third means the redundant trailing silent refresh (or a duplicate) came back.
        Assert.Equal(2, CountOccurrences(source, "NotifyAllAsync(SignalREvents.DownloadsRefresh"));
    }

    [Fact]
    public void NotifyAllAsyncInvalidatesLiveCacheBeforeTheHubSend()
    {
        var source = ReadSource("Infrastructure", "Services", "SignalRNotificationService.cs");

        var invalidate = source.IndexOf("InvalidateLiveCache()", StringComparison.Ordinal);
        var hubSend = source.IndexOf("_downloadHubContext.Clients.All.SendAsync(eventName, data)", StringComparison.Ordinal);
        Assert.True(invalidate >= 0, "NotifyAllAsync must invalidate the live dashboard cache");
        Assert.True(hubSend > invalidate, "cache invalidation must precede the hub send");
    }

    [Theory]
    [InlineData("Core", "Services", "EpicMapping", "EpicMappingService.Mapping.cs")]
    [InlineData("Core", "Services", "BattleNet", "BattleNetMappingService.cs")]
    [InlineData("Services", "Xbox", "XboxMappingService.cs")]
    public void MappingPassesStillEmitTheirOwnRefresh(params string[] pathSegments)
    {
        var source = ReadSource(pathSegments);
        Assert.Contains("NotifyAllAsync(SignalREvents.DownloadsRefresh", source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string ReadSource(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        var path = Path.Combine([root, "Api", "LancacheManager", .. pathSegments]);
        return File.ReadAllText(path);
    }
}
