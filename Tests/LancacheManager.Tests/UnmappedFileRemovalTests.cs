using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The unmapped-file removal run and its count: the refusals that stop it before any file is
/// deleted, the arguments that make the binary re-claim every file, and the exits that always end
/// its card. The Rust binary is replaced by a stand-in that writes the report the real one writes.
/// </summary>
public sealed partial class UnmappedFileRemovalTests
{
    private const string DownloadKey = "errors.unmappedRemoval.downloadInProgress";
    private const string TrackerStoppedKey = "errors.unmappedRemoval.trackerStopped";

    [Fact]
    public async Task NoFullScan_IsRefusedAsync()
    {
        await using var run = await Run.CreateAsync(fullScan: false);

        var refusal = Assert.Throws<ConflictException>(() => run.Service.EnsureUnmappedFileRemovalAllowed());

        Assert.Equal("errors.unmappedRemoval.noFullScan", refusal.StageKey);
    }

    [Fact]
    public async Task ADownload_IsRefusedAsync()
    {
        await using var run = await Run.CreateAsync();
        CacheScanGateHarness.MakeBusy(run.Snapshot);

        var refusal = Assert.Throws<DownloadInProgressException>(() => run.Service.EnsureUnmappedFileRemovalAllowed());

        Assert.Equal(DownloadKey, refusal.StageKey);
    }

    [Fact]
    public async Task ATrackerWithNoAnswerYet_IsRefusedAsTrackerStoppedAsync()
    {
        await using var run = await Run.CreateAsync();
        // A child just spawned and has not published, and no client is downloading.
        CacheScanGateHarness.SetField(run.Tracker, "_unreportedSinceUtc", DateTime.UtcNow);

        var refusal = Assert.Throws<DownloadInProgressException>(() => run.Service.EnsureUnmappedFileRemovalAllowed());

        Assert.Equal(TrackerStoppedKey, refusal.StageKey);
    }

    [Fact]
    public async Task ADownloadDuringAClaimQuery_StopsTheChildAndKeepsTheListAsync()
    {
        await using var run = await Run.CreateAsync();
        run.Rust.OnRun = async (process, _, token, _) =>
        {
            // The claim queries send no progress while they run; the download lands during one.
            await File.WriteAllTextAsync(
                Run.QuotedArgument(process, 2),
                """{"status":"querying","stageKey":"signalr.gameDetect.db.querying","percentComplete":20}""",
                CancellationToken.None);
            CacheScanGateHarness.MakeBusy(run.Snapshot);
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new ProcessExecutionResult { ExitCode = 0 };
        };
        var original = await run.StoredBucketAsync();

        var timer = Stopwatch.StartNew();
        var refusal = await Assert.ThrowsAsync<DownloadInProgressException>(() => RemovePassAsync(run));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "the pass kept running after the download started");
        Assert.Equal(DownloadKey, refusal.StageKey);
        // The child stopped before its delete stage, so every file the list names is still there.
        Assert.NotNull(original);
        Assert.Equal(original, await run.StoredBucketAsync());
    }

    [Fact]
    public async Task ADownloadAfterTheDeleteStageStarted_StopsTheChildAndClearsTheListAsync()
    {
        await using var run = await Run.CreateAsync();
        run.Rust.OnRun = async (process, _, token, _) =>
        {
            await File.WriteAllTextAsync(
                Run.QuotedArgument(process, 2),
                """{"status":"running","stageKey":"signalr.unmappedRemove.running","percentComplete":92}""",
                CancellationToken.None);
            CacheScanGateHarness.MakeBusy(run.Snapshot);
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        var refusal = await Assert.ThrowsAsync<DownloadInProgressException>(() => RemovePassAsync(run));

        Assert.Equal(DownloadKey, refusal.StageKey);
        // The stopped child may have deleted files the saved list still names.
        Assert.Null(await run.StoredBucketAsync());
    }

    [Fact]
    public async Task TheListIsClearedWhenTheDeleteStageStarts_NotOnlyWhenThePassEndsAsync()
    {
        await using var run = await Run.CreateAsync();
        string? bucketDuringTheDeletes = "not read";
        run.Rust.OnRun = async (process, _, _, onProgressEvent) =>
        {
            await File.WriteAllTextAsync(
                Run.QuotedArgument(process, 2),
                """{"status":"running","stageKey":"signalr.unmappedRemove.running","percentComplete":92}""",
                CancellationToken.None);
            await onProgressEvent!(new RustProgressEvent
            {
                Event = "progress",
                StageKey = "signalr.unmappedRemove.running",
                PercentComplete = 92
            });
            // A kill from here on ends the process before the pass can clear the list itself.
            bucketDuringTheDeletes = await run.StoredBucketAsync();
            await Run.WriteReportAsync(process, Run.RemoveReport());
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        await RemovePassAsync(run);

        Assert.Null(bucketDuringTheDeletes);
    }

    [Fact]
    public async Task ADownloadDuringTheImportPass_IsRefusedBeforeAnyDeleteAsync()
    {
        await using var run = await Run.CreateAsync();
        run.Importer.OnRead = async _ =>
        {
            CacheScanGateHarness.MakeBusy(run.Snapshot);
            await Task.Delay(TimeSpan.FromSeconds(10));
            return true;
        };

        var timer = Stopwatch.StartNew();
        var refused = await run.RemoveAsync();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "the removal waited for the import pass to end");
        Assert.Equal(OperationStatus.Failed, refused.Status);
        Assert.Equal(DownloadKey, refused.Message);
        Assert.Empty(run.Rust.Launches);
    }

    [Fact]
    public async Task ALogStepAfterTheDeleteStageStarted_StopsTheChildAndClearsTheListAsync()
    {
        await using var run = await Run.CreateAsync();
        run.Rust.OnRun = async (process, _, token, _) =>
        {
            await File.WriteAllTextAsync(
                Run.QuotedArgument(process, 2),
                """{"status":"running","stageKey":"signalr.unmappedRemove.running","percentComplete":92}""",
                CancellationToken.None);
            // A step takes the logs, so the tracker stops its child and sees no download from now on.
            CacheScanGateHarness.SetField(run.Tracker, "_childStoppedForStep", true);
            CacheScanGateHarness.SetField(run.Tracker, "_childStops", 1L);
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        var timer = Stopwatch.StartNew();
        var refusal = await Assert.ThrowsAsync<DownloadInProgressException>(() => RemovePassAsync(run));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "the pass kept running while the tracker was stopped");
        Assert.Equal(TrackerStoppedKey, refusal.StageKey);
        Assert.Null(await run.StoredBucketAsync());
    }

    [Fact]
    public async Task ATrackerRestartedSinceTheRemovalStarted_StopsTheChildAndKeepsTheListAsync()
    {
        await using var run = await Run.CreateAsync();
        // The import passes ran under the first child; a step stopped it and a new child, which
        // starts at the end of each log, reports now.
        CacheScanGateHarness.SetField(run.Tracker, "_childStops", 1L);
        run.Rust.OnRun = async (_, _, token, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        var refusal = await Assert.ThrowsAsync<DownloadInProgressException>(() => RemovePassAsync(run));

        Assert.Equal(TrackerStoppedKey, refusal.StageKey);
        Assert.NotNull(await run.StoredBucketAsync());
    }

    [Fact]
    public async Task ALogActionIsRefusedWhileTheRemovalRunsAndTheRemovalFinishesAsync()
    {
        await using var run = await Run.CreateAsync();
        ConflictException? refusal = null;
        run.Rust.OnRun = async (process, _, _, _) =>
        {
            await File.WriteAllTextAsync(
                Run.QuotedArgument(process, 2),
                """{"status":"running","stageKey":"signalr.unmappedRemove.running","percentComplete":92}""",
                CancellationToken.None);
            refusal = Assert.Throws<ConflictException>(run.Repairs.Owner.EnsureNoUnmappedRemovalRunning);
            await Run.WriteReportAsync(process, Run.RemoveReport());
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        var finished = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Completed, finished.Status);
        Assert.Equal("errors.logs.unmappedRemovalRunning", refusal?.StageKey);
        run.Repairs.Owner.EnsureNoUnmappedRemovalRunning();
    }

    // The removal starts while the harness tracker's first child reports, which has seen no stop.
    private static Task<UnmappedFilesPassResult> RemovePassAsync(Run run) =>
        run.Detection.RunUnmappedFilesPassAsync(
            Guid.NewGuid(),
            new UnmappedFilesLimits(10, 1_000_000),
            0,
            (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

    [Fact]
    public async Task AStopAfterFilesWereDeleted_ClearsTheListAsync()
    {
        await using var run = await Run.CreateAsync();
        Guid removal = default;
        run.Rust.OnRun = async (process, operationId, _, _) =>
        {
            removal = operationId!.Value;
            await Run.WriteReportAsync(process, Run.RemoveReport());
            return new ProcessExecutionResult { ExitCode = 0 };
        };
        run.DetectionLog.OnLogged = entry =>
        {
            if (entry.Message.Contains("file(s) removed", StringComparison.Ordinal))
            {
                run.Repairs.Tracker.CancelOperation(removal);
            }
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Cancelled, ended.Status);
        Assert.Null(await run.StoredBucketAsync());
    }

    [Fact]
    public async Task AStopBetweenDatasourcesAfterTheFirstDeleted_ClearsTheListAsync()
    {
        await using var run = await Run.CreateAsync(datasourceCount: 2);
        Guid removal = default;
        run.Rust.OnRun = async (process, operationId, _, _) =>
        {
            removal = operationId!.Value;
            await Run.WriteReportAsync(process, Run.RemoveReport());
            return new ProcessExecutionResult { ExitCode = 0 };
        };
        run.DetectionLog.OnLogged = entry =>
        {
            if (entry.Message.Contains("file(s) removed", StringComparison.Ordinal))
            {
                run.Repairs.Tracker.CancelOperation(removal);
            }
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Cancelled, ended.Status);
        Assert.Single(run.Rust.Launches);
        Assert.Null(await run.StoredBucketAsync());
    }

    [Fact]
    public async Task ALaterDatasourceFailure_ClearsTheListAsync()
    {
        await using var run = await Run.CreateAsync(datasourceCount: 2);
        run.Rust.OnRun = async (process, _, _, _) =>
        {
            if (run.Rust.Launches.Count == 2)
            {
                return new ProcessExecutionResult { ExitCode = 1 };
            }

            await Run.WriteReportAsync(process, Run.RemoveReport());
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Failed, ended.Status);
        Assert.Equal(2, run.Rust.Launches.Count);
        Assert.Null(await run.StoredBucketAsync());
    }

    [Fact]
    public async Task ALogTheImporterLeftUnread_IsRefusedAsLogsBehindAsync()
    {
        await using var run = await Run.CreateAsync();
        run.Importer.OnRead = _ => Task.FromResult(false);

        var refused = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Failed, refused.Status);
        Assert.Equal("errors.unmappedRemoval.logsBehind", refused.Message);
        Assert.Empty(run.Rust.Launches);
    }

    // A pass that resumed past a rotated log it could not open ends completed with that log
    // unread; no file is deleted on its word. [123]
    [Fact]
    public async Task ACompletedPassThatLeftALogUnread_DeletesNothingAsync()
    {
        await using var run = await Run.CreateAsync();
        var pass = new LogProcessingProgress
        {
            SchemaVersion = 1,
            TerminalStatus = "completed",
            FilesLeftUnread = 1,
            SourcePositions = new Dictionary<string, long> { ["access.log"] = 4 }
        };
        run.Importer.OnRead = _ => Task.FromResult(RustLogProcessorService.ReadEveryLog(pass));

        var refused = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Failed, refused.Status);
        Assert.Equal("errors.unmappedRemoval.logsBehind", refused.Message);
        Assert.Empty(run.Rust.Launches);
    }

    // An empty or unmounted log folder is named as missing logs, with or without positions saved
    // before, so a reset never turns it into a run that deletes. [122]
    [Theory]
    [InlineData(0L)]
    [InlineData(5L)]
    public async Task AnEmptyLogFolder_IsRefusedAsLogsMissingAsync(long savedPosition)
    {
        await using var run = await Run.CreateAsync();
        File.Delete(Path.Combine(run.Datasource.LogPath, "access.log"));
        if (savedPosition > 0)
        {
            run.Repairs.StateService.SetLogSourcePositions(
                run.Datasource.Name, new Dictionary<string, long> { ["access.log"] = savedPosition });
        }
        run.Importer.OnRead = _ => Task.FromResult(false);

        var refused = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Failed, refused.Status);
        Assert.Equal("errors.unmappedRemoval.logsMissing", refused.Message);
        Assert.Empty(run.Rust.Launches);
    }

    // A series read before and gone now, as when an access.log in the root stops the descent
    // into http/, is named as missing logs, not as logs behind. [124]
    [Fact]
    public async Task ASeriesReadBeforeAndMissingNow_IsRefusedAsLogsMissingAsync()
    {
        await using var run = await Run.CreateAsync();
        run.Repairs.StateService.SetLogSourcePositions(
            run.Datasource.Name, new Dictionary<string, long> { ["steam-access.log"] = 5 });
        run.Importer.OnRead = _ => Task.FromResult(false);

        var refused = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Failed, refused.Status);
        Assert.Equal("errors.unmappedRemoval.logsMissing", refused.Message);
        Assert.Empty(run.Rust.Launches);
    }

    // The request is refused naming the series until it is back in the log folder. [122]
    [Fact]
    public async Task ARequestWhileASeriesReadBeforeIsMissing_NamesItUntilItIsBackAsync()
    {
        await using var run = await Run.CreateAsync();
        run.Repairs.StateService.SetLogSourcePositions(
            run.Datasource.Name, new Dictionary<string, long> { ["access.log"] = 2, ["steam-access.log"] = 5 });

        var refused = Assert.Throws<ConflictException>(run.Service.EnsureUnmappedFileRemovalAllowed);

        Assert.Equal("errors.unmappedRemoval.logSeriesMissing", refused.StageKey);
        Assert.Equal("steam-access.log", refused.Context!["series"]);
        Assert.Equal(run.Datasource.Name, refused.Context["datasource"]);

        await File.WriteAllTextAsync(Path.Combine(run.Datasource.LogPath, "steam-access.log"), string.Empty);

        run.Service.EnsureUnmappedFileRemovalAllowed();
    }

    [Fact]
    public async Task EveryLogIsReadToTheEndBeforeTheFirstDeleteAsync()
    {
        await using var run = await Run.CreateAsync(datasourceCount: 2);
        var events = new List<string>();
        run.Importer.OnRead = datasource =>
        {
            events.Add("read " + datasource.Name);
            return Task.FromResult(true);
        };
        run.Rust.OnRun = async (process, _, _, _) =>
        {
            events.Add("remove");
            await Run.WriteReportAsync(process, Run.RemoveReport());
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Completed, ended.Status);
        Assert.Equal(["read first", "read second", "remove", "remove"], events);
    }

    [Fact]
    public async Task AnImportPassRunsWhileFilesAreBeingDeletedAsync()
    {
        await using var run = await Run.CreateAsync();
        var passStartedDuringDelete = false;
        run.Rust.OnRun = async (process, _, _, _) =>
        {
            var pass = run.Repairs.Owner.LockLogFilesAsync(
                null, OperationType.LogProcessing, LogFileLockKind.Ingest, CancellationToken.None);
            passStartedDuringDelete = await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(2))) == pass;
            await (await pass).DisposeAsync();

            await Run.WriteReportAsync(process, Run.RemoveReport());
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Completed, ended.Status);
        Assert.True(passStartedDuringDelete);
    }

    [Fact]
    public async Task CancelWhileWaitingForTheCacheLock_EndsCancelledAsync()
    {
        await using var run = await Run.CreateAsync();
        await run.CacheLock.WaitAsync();
        try
        {
            var id = (await run.Service.StartUnmappedFileRemovalAsync(new UnmappedFilesLimits(10, 1_000_000)))!.Value;
            run.Repairs.Tracker.CancelOperation(id);

            var ended = await run.FinishedAsync(id);

            Assert.Equal(OperationStatus.Cancelled, ended.Status);
            Assert.Equal(0, run.CacheLock.CurrentCount);
        }
        finally
        {
            run.CacheLock.Release();
        }
    }

    [Fact]
    public async Task ARefusalAtRunStart_EndsWithItsStageKeyAsync()
    {
        await using var run = await Run.CreateAsync();
        CacheScanGateHarness.MakeBusy(run.Snapshot);

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Failed, ended.Status);
        Assert.Equal(DownloadKey, ended.Message);
        Assert.DoesNotContain(run.Log.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.Equal(1, run.CacheLock.CurrentCount);
    }

    [Fact]
    public async Task AStopAfterTheLastDatasource_EndsCancelledAsync()
    {
        await using var run = await Run.CreateAsync();
        var original = await run.StoredBucketAsync();
        Guid removal = default;
        run.Rust.OnRun = async (process, operationId, _, _) =>
        {
            removal = operationId!.Value;
            // An empty index is the one report whose handling follows the cancel check, so a pass that
            // skipped the check would end failed instead of canceled.
            await Run.WriteReportAsync(process, Run.EmptyIndexReport());
            return new ProcessExecutionResult { ExitCode = 0 };
        };
        // The line the pass logs once it has the last report, before it saves anything.
        run.DetectionLog.OnLogged = entry =>
        {
            if (entry.Message.Contains("file(s) removed", StringComparison.Ordinal))
            {
                run.Repairs.Tracker.CancelOperation(removal);
            }
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Cancelled, ended.Status);
        Assert.Equal(original, await run.StoredBucketAsync());
    }

    [Fact]
    public async Task TempFilesAreDeletedWhenARunFailsAsync()
    {
        await using var run = await Run.CreateAsync();
        var written = new List<string>();
        run.Rust.OnRun = async (process, _, _, _) =>
        {
            written.Add(Run.QuotedArgument(process, 1));
            written.Add(Run.QuotedArgument(process, 2));
            foreach (var path in written)
            {
                await File.WriteAllTextAsync(path, "{}");
            }

            return new ProcessExecutionResult { ExitCode = 1 };
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Failed, ended.Status);
        Assert.Equal("errors.unmappedRemoval.failed", ended.Message);
        Assert.Equal(2, written.Count);
        Assert.All(written, path => Assert.False(File.Exists(path), path));
    }

    [Fact]
    public async Task AnEmptyCacheEverywhere_EndsFailedAndKeepsTheListAsync()
    {
        await using var run = await Run.CreateAsync();
        var original = await run.StoredBucketAsync();
        run.Rust.Report = Run.EmptyIndexReport();

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Failed, ended.Status);
        Assert.Equal("errors.unmappedRemoval.cacheEmpty", ended.Message);
        Assert.Equal(original, await run.StoredBucketAsync());
    }

    [Fact]
    public async Task ASuccessfulRemovalClearsTheCachedDetectionAsync()
    {
        await using var run = await Run.CreateAsync();
        var cachedField = typeof(GameCacheDetectionService)
            .GetField("_cachedDetectionResponse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        cachedField.SetValue(run.Detection, new GameCacheDetectionService.DetectionOperationResponse());

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Completed, ended.Status);
        Assert.Null(cachedField.GetValue(run.Detection));
    }

    [Fact]
    public async Task TheUnmappedCountDoesNotWaitForTheCacheLockAsync()
    {
        await using var run = await Run.CreateAsync();
        run.Rust.Report = new Dictionary<string, object?>
        {
            ["total_games_detected"] = 0,
            ["total_services_detected"] = 0,
            ["indexed_cache_files"] = 10,
            ["games"] = Array.Empty<GameCacheInfo>(),
            ["services"] = Array.Empty<ServiceCacheInfo>(),
            ["unmapped_services"] = new[] { new UnmappedService { Service = "steam", FileCount = 5, TotalSizeBytes = 5000 } },
            ["unmapped_eligible"] = new[] { new UnmappedService { Service = "steam", FileCount = 5, TotalSizeBytes = 5000 } }
        };
        await run.CacheLock.WaitAsync();
        try
        {
            var files = await run.Service
                .CountUnmappedCacheFilesAsync(Guid.NewGuid(), (_, _) => Task.CompletedTask, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(5, files);
            Assert.Contains("--unmapped-files count", Assert.Single(run.Rust.Launches), StringComparison.Ordinal);
        }
        finally
        {
            run.CacheLock.Release();
        }
    }

    [Fact]
    public async Task RemoveRun_PassesTheFullClaimArgumentsAsync()
    {
        await using var run = await Run.CreateAsync();

        var ended = await run.RemoveAsync(new UnmappedFilesLimits(12, 3456));

        Assert.Equal(OperationStatus.Completed, ended.Status);
        var arguments = Assert.Single(run.Rust.Launches);
        Assert.Contains("--unmapped-files remove", arguments, StringComparison.Ordinal);
        Assert.Contains("--max-files 12", arguments, StringComparison.Ordinal);
        Assert.Contains("--max-bytes 3456", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--incremental", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--skip-service-scan", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("excluded_game_ids", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverLimitReport_EndsTheRunWithMoreThanConfirmedAsync()
    {
        await using var run = await Run.CreateAsync();
        string? messageDuringRun = null;
        run.Rust.OnRun = async (process, operationId, _, onProgress) =>
        {
            await File.WriteAllTextAsync(
                Run.QuotedArgument(process, 2),
                """{"status":"running","stageKey":"signalr.gameDetect.scanning","percentComplete":50}""");
            await onProgress!(new RustProgressEvent());
            messageDuringRun = run.Repairs.Tracker.GetOperation(operationId!.Value)!.Message;
            await Run.WriteReportAsync(process, Run.RemoveReport(overLimit: true, removedFiles: 0));
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Failed, ended.Status);
        Assert.Equal("errors.unmappedRemoval.moreThanConfirmed", ended.Message);
        Assert.Equal("signalr.unmappedRemove.running", messageDuringRun);
        var saved = JsonSerializer.Deserialize<List<UnmappedService>>((await run.StoredBucketAsync())!)!;
        var remaining = Assert.Single(saved);
        Assert.Equal("steam", remaining.Service);
        Assert.Equal(5, remaining.FileCount);
    }

    [Fact]
    public async Task ADownloadAfterTheLastDelete_StillSavesTheListAsync()
    {
        await using var run = await Run.CreateAsync();
        run.Rust.OnRun = async (process, _, token, onProgress) =>
        {
            await Run.WriteReportAsync(process, Run.RemoveReport());
            CacheScanGateHarness.MakeBusy(run.Snapshot);
            // The events the binary sends while it writes its report, after the delete loop.
            await onProgress!(new RustProgressEvent { Event = "progress", StageKey = "signalr.gameDetect.writing" });
            await onProgress(new RustProgressEvent { Event = "complete", StageKey = "signalr.gameDetect.complete.default" });
            // The real helper reads the next line with the token, so a cancel from a callback throws.
            token.ThrowIfCancellationRequested();
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Completed, ended.Status);
        var saved = JsonSerializer.Deserialize<List<UnmappedService>>((await run.StoredBucketAsync())!)!;
        Assert.Equal("steam", Assert.Single(saved).Service);
    }

    [Fact]
    public async Task ADownloadWhileTheEmptiedFoldersAreRemoved_StillSavesTheListAsync()
    {
        await using var run = await Run.CreateAsync();
        run.Rust.OnRun = async (process, _, token, onProgress) =>
        {
            // The binary's order after its last delete: the writing event, the report, then the
            // folder removal with its own progress rows.
            await onProgress!(new RustProgressEvent { Event = "progress", StageKey = "signalr.gameDetect.writing" });
            await Run.WriteReportAsync(process, Run.RemoveReport());
            CacheScanGateHarness.MakeBusy(run.Snapshot);
            await WriteProgressAsync(process, "signalr.unmappedRemove.running", 97);
            await onProgress(new RustProgressEvent { Event = "progress", StageKey = "signalr.unmappedRemove.running" });
            await Task.Delay(TimeSpan.FromMilliseconds(200), token);
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Completed, ended.Status);
        var saved = JsonSerializer.Deserialize<List<UnmappedService>>((await run.StoredBucketAsync())!)!;
        Assert.Equal("steam", Assert.Single(saved).Service);
    }

    [Fact]
    public async Task AStopWhileTheEmptiedFoldersAreRemoved_EndsCancelledAndClearsTheListAsync()
    {
        await using var run = await Run.CreateAsync();
        run.Rust.OnRun = async (process, operationId, token, onProgress) =>
        {
            await onProgress!(new RustProgressEvent { Event = "progress", StageKey = "signalr.gameDetect.writing" });
            await Run.WriteReportAsync(process, Run.RemoveReport());
            await WriteProgressAsync(process, "signalr.unmappedRemove.running", 97);
            await onProgress(new RustProgressEvent { Event = "progress", StageKey = "signalr.unmappedRemove.running" });
            run.Repairs.Tracker.CancelOperation(operationId!.Value);
            token.ThrowIfCancellationRequested();
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Cancelled, ended.Status);
        // Every file the report names was deleted, so the saved list no longer holds.
        Assert.Null(await run.StoredBucketAsync());
    }

    [Fact]
    public async Task ALateEventReadingTheReportOrFinalRow_DoesNotMoveTheBarAsync()
    {
        await using var run = await Run.CreateAsync();
        var seen = new List<double>();
        run.Rust.OnRun = async (process, operationId, _, onProgress) =>
        {
            // The child writes on without waiting for the host, so a folder event can be handled
            // after the file already holds a later row.
            foreach (var (stageKey, percent) in new[]
            {
                ("signalr.unmappedRemove.running", 99.0),
                ("signalr.gameDetect.writing", 92.0),
                ("signalr.gameDetect.complete.default", 100.0)
            })
            {
                await WriteProgressAsync(process, stageKey, percent);
                await onProgress!(new RustProgressEvent { Event = "progress", StageKey = "signalr.unmappedRemove.running" });
                seen.Add(run.Repairs.Tracker.GetOperation(operationId!.Value)!.PercentComplete);
            }

            await Run.WriteReportAsync(process, Run.RemoveReport());
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Completed, ended.Status);
        Assert.Equal(new[] { 99.0, 99.0, 99.0 }, seen);
    }

    [Fact]
    public async Task TwoDatasources_MoveTheBarOnlyForwardAndReach100OnlyAtTheEndAsync()
    {
        await using var run = await Run.CreateAsync(datasourceCount: 2);
        var seen = new List<double>();
        run.Rust.OnRun = async (process, operationId, _, onProgress) =>
        {
            async Task HandleAsync(string stageKey)
            {
                await onProgress!(new RustProgressEvent { Event = "progress", StageKey = stageKey });
                seen.Add(run.Repairs.Tracker.GetOperation(operationId!.Value)!.PercentComplete);
            }

            // Each child's rows in the order it writes them. The host handles some events only
            // after the child wrote its next row: the last delete event after the report row, and
            // the last folder event after the final row.
            await WriteProgressAsync(process, "signalr.unmappedRemove.running", 50);
            await HandleAsync("signalr.unmappedRemove.running");
            await WriteProgressAsync(process, "signalr.unmappedRemove.running", 95.5);
            await HandleAsync("signalr.unmappedRemove.running");
            await WriteProgressAsync(process, "signalr.gameDetect.writing", 92);
            await HandleAsync("signalr.unmappedRemove.running");
            await HandleAsync("signalr.gameDetect.writing");
            await Run.WriteReportAsync(process, Run.RemoveReport());
            await WriteProgressAsync(process, "signalr.unmappedRemove.running", 99);
            await HandleAsync("signalr.unmappedRemove.running");
            await WriteProgressAsync(process, "signalr.gameDetect.complete.default", 100);
            await HandleAsync("signalr.unmappedRemove.running");
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Completed, ended.Status);
        Assert.Equal(2, run.Rust.Launches.Count);
        Assert.Equal(12, seen.Count);
        for (var index = 1; index < seen.Count; index++)
        {
            Assert.True(seen[index] >= seen[index - 1], $"the bar went from {seen[index - 1]} to {seen[index]}");
        }

        Assert.All(seen, percent => Assert.True(percent < 100, $"the bar read {percent} before the run ended"));
        Assert.Equal(100, ended.PercentComplete);
    }

    private static Task WriteProgressAsync(ProcessStartInfo process, string stageKey, double percent) =>
        File.WriteAllTextAsync(
            Run.QuotedArgument(process, 2),
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["status"] = "running",
                ["stageKey"] = stageKey,
                ["percentComplete"] = percent
            }),
            CancellationToken.None);

    [Fact]
    public async Task ADownloadSeenOnTheLastLoopEventAfterTheChildFinished_StillSavesTheListAsync()
    {
        await using var run = await Run.CreateAsync();
        run.Rust.OnRun = async (process, _, token, onProgress) =>
        {
            // The child printed its last loop event, then deleted the last file and wrote its
            // report before the parent handled that event.
            await Run.WriteReportAsync(process, Run.RemoveReport());
            CacheScanGateHarness.MakeBusy(run.Snapshot);
            await onProgress!(new RustProgressEvent { Event = "progress", StageKey = "signalr.unmappedRemove.running" });
            token.ThrowIfCancellationRequested();
            return new ProcessExecutionResult { ExitCode = 0 };
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Completed, ended.Status);
        var saved = JsonSerializer.Deserialize<List<UnmappedService>>((await run.StoredBucketAsync())!)!;
        Assert.Equal("steam", Assert.Single(saved).Service);
    }

    [Fact]
    public async Task AStopThatSurfacesAfterTheListCommitted_EndsCompletedAsync()
    {
        await using var run = await Run.CreateAsync();
        Guid removal = default;
        run.Rust.OnRun = async (process, operationId, _, _) =>
        {
            removal = operationId!.Value;
            await Run.WriteReportAsync(process, Run.RemoveReport());
            return new ProcessExecutionResult { ExitCode = 0 };
        };
        // The database commits the list, and the Stop reaches the provider before its reply does.
        run.Saves.OnSaved = token =>
        {
            run.Saves.OnSaved = null;
            run.Repairs.Tracker.CancelOperation(removal);
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Completed, ended.Status);
        var saved = JsonSerializer.Deserialize<List<UnmappedService>>((await run.StoredBucketAsync())!)!;
        Assert.Equal("steam", Assert.Single(saved).Service);
    }

    [Fact]
    public async Task ASummaryRefreshFailureAfterTheSave_EndsCompletedAsync()
    {
        await using var run = await Run.CreateAsync();
        var saves = 0;
        // The first save stores the list; the second is the disk summary refresh.
        run.Saves.OnSaved = _ => ++saves == 2
            ? throw new InvalidOperationException("The database connection was lost.")
            : Task.CompletedTask;

        var ended = await run.RemoveAsync();

        Assert.Equal(OperationStatus.Completed, ended.Status);
        Assert.Equal(2, saves);
        var saved = JsonSerializer.Deserialize<List<UnmappedService>>((await run.StoredBucketAsync())!)!;
        Assert.Equal("steam", Assert.Single(saved).Service);
    }

    /// <summary>
    /// One real removal service wired over a stand-in for the Rust binary, a real tracker, log
    /// lock and detection service, and an in-memory store for the unmapped list.
    /// </summary>
    private sealed partial class Run : IAsyncDisposable
    {
        private readonly string _root;

        private Run(string root, OperationRepairTests.RepairHarness repairs, int datasourceCount)
        {
            _root = root;
            Repairs = repairs;
            Paths = new AuthCredentialFormatTests.TempDirPathResolver(root);
            Rust = new ScriptedRust(Paths, repairs.Tracker);
            Contexts = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase("unmapped-file-removal-" + Guid.NewGuid().ToString("N"))
                .AddInterceptors(Saves)
                .Options);
            Store = new GameCacheDetectionDataService(Contexts, NullLogger<GameCacheDetectionDataService>.Instance);

            // Own folders: other test classes write the default relative logs/access.log in parallel.
            var settings = new Dictionary<string, string?>
            {
                ["LanCache:LogPath"] = Path.Combine(root, "logs"),
                ["LanCache:CachePath"] = Path.Combine(root, "cache")
            };
            if (datasourceCount == 2)
            {
                foreach (var (index, name) in new[] { (0, "first"), (1, "second") })
                {
                    Directory.CreateDirectory(Path.Combine(root, name, "logs"));
                    Directory.CreateDirectory(Path.Combine(root, name, "cache"));
                    settings[$"LanCache:DataSources:{index}:Name"] = name;
                    settings[$"LanCache:DataSources:{index}:LogPath"] = Path.Combine(root, name, "logs");
                    settings[$"LanCache:DataSources:{index}:CachePath"] = Path.Combine(root, name, "cache");
                }
            }

            var datasources = new DatasourceService(
                new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
                Paths,
                NullLogger<DatasourceService>.Instance);
            Assert.Equal(datasourceCount, datasources.GetDatasources().Count);
            Datasource = datasources.GetDatasources()[0];
            foreach (var datasource in datasources.GetDatasources())
            {
                Directory.CreateDirectory(datasource.LogPath);
                File.WriteAllText(Path.Combine(datasource.LogPath, "access.log"), string.Empty);
            }
            var binary = Paths.GetRustGameDetectorPath();
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.WriteAllText(binary, string.Empty);

            Tracker = CacheScanGateHarness.TrackerWith(Snapshot, []);
            // Stands in for a child that keeps publishing, so it never falls silent during a test.
            CacheScanGateHarness.SetField(Tracker, "_reportDueUtc", DateTime.MaxValue);
            var gate = CacheScanGateHarness.GateOver(Tracker);
            Detection = new GameCacheDetectionService(
                DetectionLog,
                Paths,
                repairs.Owner,
                Contexts,
                Store,
                evictedDetectionPreservationService: null!,
                unknownGameResolutionService: null!,
                Rust,
                Notifications,
                datasources,
                new DatasourceCapabilityService(datasources),
                repairs.Tracker,
                gate);
            Service = CacheFileCountContractTests.NewServiceForCountState(
                repairs.Tracker,
                Notifications,
                Paths,
                Rust,
                Detection,
                repairs.StateService,
                gate,
                repairs.Owner,
                Log,
                datasources,
                Importer);
        }

        public OperationRepairTests.RepairHarness Repairs { get; }
        public AuthCredentialFormatTests.TempDirPathResolver Paths { get; }
        public ScriptedRust Rust { get; }
        public ScriptedImporter Importer { get; } = new();
        public DownloadSpeedSnapshot Snapshot { get; } = new();
        public RustSpeedTrackerService Tracker { get; }
        public ScheduledRunReporterTests.CapturingNotificationService Notifications { get; } = new();
        public CapturingLogger<CacheManagementService> Log { get; } = new();
        public CapturingLogger<GameCacheDetectionService> DetectionLog { get; } = new();
        public TestDbContextFactory Contexts { get; }
        public SaveHook Saves { get; } = new();
        public GameCacheDetectionDataService Store { get; }
        public GameCacheDetectionService Detection { get; }
        public CacheManagementService Service { get; }
        public ResolvedDatasource Datasource { get; }

        public SemaphoreSlim CacheLock => (SemaphoreSlim)typeof(CacheManagementService)
            .GetField("_cacheLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Service)!;

        public static async Task<Run> CreateAsync(bool fullScan = true, int datasourceCount = 1)
        {
            var root = Path.Combine(Path.GetTempPath(), "lm-unmapped-removal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var repairs = await OperationRepairTests.RepairHarness.CreateAsync(Path.Combine(root, "state"), start: false);
            var run = new Run(root, repairs, datasourceCount);
            if (fullScan)
            {
                repairs.StateService.SetGameDetectionLastFullScan(DateTime.UtcNow);
            }

            await run.Store.SaveUnmappedServicesAsync(
                [new UnmappedService { Service = "wsus", FileCount = 7, TotalSizeBytes = 4096 }],
                CancellationToken.None);
            return run;
        }

        /// <summary>Starts the removal and waits until the run has ended and released its locks.</summary>
        public async Task<OperationInfo> RemoveAsync(UnmappedFilesLimits? limits = null)
        {
            var id = (await Service.StartUnmappedFileRemovalAsync(limits ?? new UnmappedFilesLimits(10, 1_000_000)))!.Value;
            return await FinishedAsync(id);
        }

        public async Task<OperationInfo> FinishedAsync(Guid operationId)
        {
            await Notifications.WhenEventAsync(SignalREvents.UnmappedFileRemovalComplete).WaitAsync(TimeSpan.FromSeconds(10));
            return Repairs.Tracker.GetOperation(operationId)!;
        }

        public async Task<string?> StoredBucketAsync()
        {
            await using var context = await Contexts.CreateDbContextAsync();
            return (await context.CachedDetectionSummaries.SingleOrDefaultAsync())?.UnmappedServicesJson;
        }

        public static Dictionary<string, object?> RemoveReport(bool overLimit = false, long removedFiles = 3) => new()
        {
            ["total_games_detected"] = 0,
            ["total_services_detected"] = 0,
            ["indexed_cache_files"] = 10,
            ["games"] = Array.Empty<GameCacheInfo>(),
            ["services"] = Array.Empty<ServiceCacheInfo>(),
            ["unmapped_services"] = new[] { new UnmappedService { Service = "steam", FileCount = 5, TotalSizeBytes = 5000 } },
            ["unmapped_removed_files"] = removedFiles,
            ["unmapped_removed_bytes"] = removedFiles * 1000,
            ["unmapped_remove_failures"] = 0,
            ["unmapped_over_limit"] = overLimit
        };

        public static Dictionary<string, object?> EmptyIndexReport() => new()
        {
            ["total_games_detected"] = 0,
            ["total_services_detected"] = 0,
            ["indexed_cache_files"] = 0,
            ["games"] = Array.Empty<GameCacheInfo>(),
            ["services"] = Array.Empty<ServiceCacheInfo>(),
            ["unmapped_services"] = Array.Empty<UnmappedService>(),
            ["unmapped_removed_files"] = 0,
            ["unmapped_removed_bytes"] = 0,
            ["unmapped_remove_failures"] = 0,
            ["unmapped_over_limit"] = false
        };

        /// <summary>The quoted arguments of the detector are the cache root, the report and the progress file.</summary>
        public static string QuotedArgument(ProcessStartInfo process, int index) =>
            QuotedRegex().Matches(process.Arguments)[index].Groups[1].Value;

        public static Task WriteReportAsync(ProcessStartInfo process, object report) =>
            File.WriteAllTextAsync(QuotedArgument(process, 1), JsonSerializer.Serialize(report));

        public async ValueTask DisposeAsync()
        {
            await Repairs.DisposeAsync();
            Detection.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A file the process still holds must not fail the test.
            }
        }

        [GeneratedRegex("\"([^\"]*)\"")]
        private static partial Regex QuotedRegex();
    }

    /// <summary>Runs <see cref="OnSaved"/> after each save the database has committed.</summary>
    private sealed class SaveHook : SaveChangesInterceptor
    {
        public Func<CancellationToken, Task>? OnSaved { get; set; }

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData saved,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (OnSaved is { } onSaved)
            {
                await onSaved(cancellationToken);
            }

            return result;
        }
    }

    private sealed class ScriptedRust(IPathResolver paths, IUnifiedOperationTracker tracker)
        : RustProcessHelper(
            NullLogger<RustProcessHelper>.Instance,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            paths,
            tracker)
    {
        public List<string> Launches { get; } = [];

        public Dictionary<string, object?> Report { get; set; } = Run.RemoveReport();

        public Func<ProcessStartInfo, Guid?, CancellationToken, Func<RustProgressEvent, Task>?, Task<ProcessExecutionResult>>? OnRun { get; set; }

        public override async Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
            ProcessStartInfo process,
            Guid? operationId,
            CancellationToken cancellationToken,
            Func<RustProgressEvent, Task>? onProgressEvent,
            string processLabel = "rust")
        {
            Launches.Add(process.Arguments);
            if (OnRun != null)
            {
                return await OnRun(process, operationId, cancellationToken, onProgressEvent);
            }

            await Run.WriteReportAsync(process, Report);
            return new ProcessExecutionResult { ExitCode = 0 };
        }
    }

    /// <summary>Stands in for the import pass a removal runs before it deletes anything.</summary>
    internal sealed class ScriptedImporter() : RustLogProcessorService(
        null!, null!, null!, null!, null!, null!, null!, null!, null!)
    {
        public Func<ResolvedDatasource, Task<bool>> OnRead { get; set; } = _ => Task.FromResult(true);

        internal override Task<bool> ReadToEndAsync(ResolvedDatasource datasource, CancellationToken cancellationToken) =>
            OnRead(datasource);
    }
}
