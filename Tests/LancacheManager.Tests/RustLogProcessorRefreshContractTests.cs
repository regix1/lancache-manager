using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
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
    private static LogProcessingProgress TerminalCheckpoint(
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
        Assert.False(RustLogProcessorService.HasCommittedDownloads(new LogProcessingProgress
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
        var source = ReadSource("Infrastructure", "Services", "Rust", "RustLogProcessorService.cs");

        var committedEmit = source.IndexOf("await NotifyCommittedDownloadsAsync(finalProgress);", StringComparison.Ordinal);
        var autoTag = source.IndexOf("await AutoTagNewDownloadsAsync();", StringComparison.Ordinal);
        var epicResolve = source.IndexOf("epicMappingService.ResolveDownloadsAsync()", StringComparison.Ordinal);
        var blizzardResolve = source.IndexOf("battleNetMappingService.ResolveDownloadsAsync(", StringComparison.Ordinal);
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
        var source = ReadSource("Infrastructure", "Services", "Rust", "RustLogProcessorService.cs");

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
        var source = ReadSource("Infrastructure", "Services", "Rust", "RustLogProcessorService.cs");

        // Fire-and-forget skips the dashboard cache-generation bump that must precede the
        // hub send, so it can serve a refetch a stale batch. It must never appear here.
        Assert.DoesNotContain("NotifyAllFireAndForget", source, StringComparison.Ordinal);

        // Exactly two emission sites: the committed-boundary helper and the auto-tag pass.
        // A third means the redundant trailing silent refresh (or a duplicate) came back.
        Assert.Equal(2, CountOccurrences(source, "NotifyAllAsync(SignalREvents.DownloadsRefresh"));
    }

    [Fact]
    public void IngestionScopesDuplicatesAndActiveDownloadsToDatasource()
    {
        var source = ReadRepositorySource(["rust-processor", "src", "log_processor.rs"]);

        Assert.Contains("WHERE \"Datasource\" = $6", source, StringComparison.Ordinal);
        Assert.Contains("WHERE \\\"Id\\\" = $10 AND \\\"Datasource\\\" = $11", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\"Datasource\\\" IS NULL", source, StringComparison.Ordinal);

        var activeStatements = source.Split('\n')
            .Where(line =>
                line.Contains("\\\"Downloads\\\"", StringComparison.Ordinal) &&
                (line.Contains("SELECT", StringComparison.Ordinal) || line.Contains("UPDATE", StringComparison.Ordinal)))
            .ToArray();

        Assert.Equal(11, activeStatements.Length);
        Assert.All(activeStatements, statement =>
            Assert.Contains("\\\"Datasource\\\" = $", statement, StringComparison.Ordinal));
        Assert.DoesNotContain("SET \\\"IsActive\\\" = false", source, StringComparison.Ordinal);
        Assert.Equal(10, CountOccurrences(source, "\\\"IsEvicted\\\" = false"));

        var update = Assert.Single(activeStatements, line =>
            line.Contains("UPDATE \\\"Downloads\\\"", StringComparison.Ordinal));
        Assert.Contains("GREATEST(\\\"EndTimeUtc\\\", $1)", update, StringComparison.Ordinal);
        Assert.Contains("LEAST(\\\"StartTimeUtc\\\", $12)", update, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(source, "LOCK TABLE \\\"Downloads\\\" IN ROW EXCLUSIVE MODE"));
        Assert.DoesNotContain(activeStatements.Where(line => line.Contains("SELECT", StringComparison.Ordinal)),
            line => line.Contains("\\\"IsActive\\\" = true", StringComparison.Ordinal));
        Assert.Equal(2, CountOccurrences(update, "$1 > \\\"EndTimeUtc\\\""));
        Assert.DoesNotContain("$1 >= \\\"EndTimeUtc\\\"", update, StringComparison.Ordinal);
        Assert.Contains("\\\"Service\\\" = CASE WHEN \\\"GameName\\\" IS NULL THEN $13 ELSE \\\"Service\\\" END", update, StringComparison.Ordinal);

        var adoption = Assert.Single(activeStatements, line =>
            line.Contains("strpos(lower(", StringComparison.Ordinal));
        Assert.Contains("\\\"LogEntries\\\"", adoption, StringComparison.Ordinal);
    }

    [Fact]
    public void NotifyAllAsyncInvalidatesLiveCacheBeforeTheHubSend()
    {
        var source = ReadSource("Infrastructure", "Services", "SignalRNotificationService.cs");

        var invalidate = source.IndexOf("InvalidateLiveCache()", StringComparison.Ordinal);
        var hubSend = source.IndexOf("ClientsFor(eventName).SendAsync(eventName, data)", StringComparison.Ordinal);
        Assert.True(invalidate >= 0, "NotifyAllAsync must invalidate the live dashboard cache");
        Assert.True(hubSend > invalidate, "cache invalidation must precede the hub send");
    }

    [Fact]
    public void AnInteractiveRunCompletesTheMomentTheProcessorFinishes()
    {
        var source = ReadSource("Infrastructure", "Services", "Rust", "RustLogProcessorService.cs");

        // The busy flag still clears before the operation completes, so polling sees the run end,
        // and nothing waits in between.
        var finalize = source.IndexOf("if (!liveIngest && shouldFinalizeOperation)", StringComparison.Ordinal);
        Assert.True(finalize >= 0, "the interactive finalize branch is missing");
        var cleared = source.IndexOf("if (_currentOperationId == ownerOperationId) IsProcessing = false;", finalize, StringComparison.Ordinal);
        var completed = source.IndexOf("_operationTracker.CompleteOperation(ownerOperationId.Value, true", finalize, StringComparison.Ordinal);
        Assert.True(cleared > finalize, "the interactive finalize branch must clear IsProcessing");
        Assert.True(completed > cleared, "IsProcessing must clear before the operation completes");
        Assert.DoesNotContain("Task.Delay(", source[finalize..completed], StringComparison.Ordinal);
    }

    [Fact]
    public void MappingRunsInsideLogProcessingKeepTheirNotices()
    {
        // The Rust processor is not built for tests, so these call sites are read, not run.
        var processor = Regex.Replace(
            ReadSource("Infrastructure", "Services", "Rust", "RustLogProcessorService.cs"), @"\s+", "");
        var startup = Regex.Replace(
            ReadSource("Infrastructure", "Services", "Mapping", "BattleNetMappingStartupResolveService.cs"), @"\s+", "");

        // Riot: a full card during an interactive run; nothing unless it fails during live ingest.
        Assert.Contains(
            "processingToken,liveIngest?newRunNotice(NotificationMode.Hidden,RunTrigger.Scheduled):newRunNotice(NotificationMode.All,RunTrigger.Manual),",
            processor, StringComparison.Ordinal);
        // Battle.net: a background row during an interactive run; nothing unless it fails otherwise.
        Assert.Contains(
            "battleNetMappingService.ResolveDownloadsAsync(liveIngest?newRunNotice(NotificationMode.Hidden,RunTrigger.Scheduled):newRunNotice(NotificationMode.Silent,RunTrigger.Manual))",
            processor, StringComparison.Ordinal);
        Assert.Contains(
            "_mappingService.ResolveDownloadsAsync(newRunNotice(NotificationMode.Hidden,RunTrigger.Startup),stoppingToken)",
            startup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Core", "Services", "EpicMapping", "EpicMappingService.Mapping.cs")]
    [InlineData("Core", "Services", "BattleNet", "BattleNetMappingService.cs")]
    [InlineData("Core", "Services", "Xbox", "XboxMappingService.cs")]
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
        return ReadRepositorySource(["Api", "LancacheManager", .. pathSegments]);
    }

    private static string ReadRepositorySource(
        string[] pathSegments,
        [CallerFilePath] string sourcePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Repository root not found");
        }

        var path = Path.Combine([directory.FullName, .. pathSegments]);
        return File.ReadAllText(path);
    }
}
