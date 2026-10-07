using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Configuration;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using static LancacheManager.Tests.CacheScanGateHarness;

namespace LancacheManager.Tests;

/// <summary>
/// The first phase of every eviction scan is a full game detection run under the scan's own
/// operation: started with its card hidden, its percent forwarded onto the scan card, waited on
/// until its terminal, and cancelled along with the scan. The tracker here is a stand-in the test
/// drives, so the detection stays "running" exactly as long as each test wants it to.
/// </summary>
// A registry holding a run starts it when the process-wide downloads-ended event fires, so these
// tests share that event's collection and never see another class raise it mid-test.
[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class CacheScanDetectionPhaseTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RemovalStartsUnderActiveScanAndCancellationKeepsCompetitorsWaiting(bool cancel, bool populated)
    {
        using var ctx = new PhaseContext();
        await using var database = await TestDatabase.CreateAsync();
        await using var context = new AppDbContext(database.Options);
        foreach (var platform in populated ? Enum.GetValues<PrefillPlatform>() : [])
        {
            context.PrefillCachedApps.Add(new PrefillCachedApp
            {
                Platform = platform, AppId = "123", AppName = "Removed", CachedAtUtc = DateTime.UtcNow
            });
            context.PrefillCachedApps.Add(new PrefillCachedApp
            {
                Platform = platform, AppId = "456", AppName = "Kept", CachedAtUtc = DateTime.UtcNow
            });
            context.Downloads.Add(new Download
            {
                Service = platform.ToService(), ClientIp = "127.0.0.1", Datasource = "Default",
                GameAppId = platform == PrefillPlatform.Steam ? 123 : null,
                EpicAppId = platform == PrefillPlatform.Epic ? "123" : null,
                XboxProductId = platform == PrefillPlatform.Xbox ? "123" : null,
                GameName = "Removed", IsEvicted = true, StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var scanId = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        var queue = new OperationQueueService(tracker,
            new OperationConflictChecker(
                tracker,
                ctx._operationStateService,
                NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var promoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = await queue.EnqueueAsync(OperationType.CacheSizeScan, ConflictScope.Bulk(), "Cache File Scan",
            () => { promoted.TrySetResult(); return Task.FromResult<Guid?>(Guid.NewGuid()); }, CancellationToken.None);
        Assert.True(queued.Queued);
        var terminals = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var childTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.OperationTerminal += operation =>
        {
            terminals.Add(operation.Id);
            if (operation.Type == OperationType.EvictionRemoval) childTerminal.TrySetResult();
            if (operation.Id == scanId) scanTerminal.TrySetResult();
        };
        Guid removalId = default;
        ctx.Notifications.OnSent = (eventName, value) =>
        {
            if (eventName != SignalREvents.EvictionRemovalStarted || value is not EvictionRemovalStarted started) return;
            removalId = started.OperationId;
            Assert.Equal(OperationStatus.Running, tracker.GetOperation(scanId)!.Status);
            Assert.Equal(OperationStatus.Running, tracker.GetOperation(removalId)!.Status);
            Assert.False(promoted.Task.IsCompleted);
            if (cancel) tracker.CancelOperation(removalId);
        };
        await ctx.WaitForRepairAsync(
            ctx.Scan.RemoveEvictedRecordsAsync(context, CancellationToken.None),
            TimeSpan.FromSeconds(5));
        Assert.NotEqual(Guid.Empty, removalId);
        Assert.Equal(cancel ? OperationStatus.Cancelled : OperationStatus.Completed,
            tracker.GetOperation(removalId)!.Status);
        Assert.Equal(populated ? cancel ? 10 : 5 : 0, await context.PrefillCachedApps.CountAsync());
        if (!cancel) Assert.All(await context.PrefillCachedApps.AsNoTracking().ToListAsync(), app => Assert.Equal("456", app.AppId));
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(scanId)!.Status);
        Assert.False(promoted.Task.IsCompleted);
        Assert.Equal(queued.OperationId, Assert.Single(tracker.GetWaitingOperations()).Id);
        // The removal's repair holds the queue as well, and it reports its end to the stand-in
        // tracker, not to this one, so the scan ends only after the repair has.
        await WaitUntilAsync(() => ctx._operationStateService.GetBlockingRepair() is null);
        tracker.CompleteOperation(scanId, success: true);
        await ctx.WaitForRepairAsync(promoted.Task, TimeSpan.FromSeconds(5));
        await Task.WhenAll(childTerminal.Task, scanTerminal.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, terminals.Count(id => id == removalId));
        Assert.Equal(1, terminals.Count(id => id == scanId));
    }

    [Fact]
    public async Task ARemoveModeScanLeavesEvictedRecordsWhileAGameDetectionRunsAsync()
    {
        using var ctx = new PhaseContext();
        await using var database = await TestDatabase.CreateAsync();
        await using var context = new AppDbContext(database.Options);
        context.Downloads.Add(new Download
        {
            Service = PrefillPlatform.Steam.ToService(), ClientIp = "127.0.0.1", Datasource = "Default", GameAppId = 123,
            GameName = "Removed", IsEvicted = true, StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var detectionId = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource());

        await ctx.Scan.RemoveEvictedRecordsAsync(context, CancellationToken.None);

        Assert.Equal(0, ctx.Notifications.Count(SignalREvents.EvictionRemovalStarted));
        Assert.Empty(tracker.GetActiveOperations(OperationType.EvictionRemoval));
        Assert.True(await context.Downloads.AnyAsync(download => download.IsEvicted));

        // Once the detection has ended, the next scan's cleanup removes the record.
        tracker.CompleteOperation(detectionId, success: true);
        await ctx.WaitForRepairAsync(
            ctx.Scan.RemoveEvictedRecordsAsync(context, CancellationToken.None),
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => ctx._operationStateService.GetBlockingRepair() is null);
        Assert.Equal(1, ctx.Notifications.Count(SignalREvents.EvictionRemovalStarted));
        Assert.False(await context.Downloads.AnyAsync(download => download.IsEvicted));
    }

    [Fact]
    public async Task AnEvictionRemovalWhoseLogFolderIsMissingNamesItOnItsCardAsync()
    {
        using var ctx = new PhaseContext();
        await using var database = await TestDatabase.CreateAsync();
        await using var context = new AppDbContext(database.Options);
        context.Downloads.Add(new Download
        {
            Service = PrefillPlatform.Steam.ToService(), ClientIp = "127.0.0.1", Datasource = "Default", GameAppId = 123,
            GameName = "Removed", IsEvicted = true, StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        // The removal's warning is set through the repair owner, which must write to the same tracker.
        typeof(OperationStateService)
            .GetField("_operationTracker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(ctx._operationStateService, tracker);
        var source = Assert.Single(ctx.Datasources.GetDatasources());
        // The key recipe is configured, so the missing folder does not turn the evidence unknown first.
        source.SchemeOverride = DatasourceSchemeOverride.Monolithic;
        Directory.Delete(source.LogPath, recursive: true);

        await ctx.WaitForRepairAsync(
            ctx.Scan.RemoveEvictedRecordsAsync(context, CancellationToken.None),
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => ctx._operationStateService.GetBlockingRepair() is null);

        var removal = Assert.Single(tracker.GetRuns().Runs,
            run => run.OperationType == OperationType.EvictionRemoval.ToWireString());
        var warning = Assert.Single(removal.Warnings);
        Assert.Equal("common.notifications.warnings.logFoldersMissing", warning.StageKey);
        Assert.Equal(source.Name, warning.Context["datasources"]);
        Assert.True(removal.Retained);
        Assert.False(await context.Downloads.AnyAsync(download => download.IsEvicted));
    }

    [Fact]
    public async Task AnEvictionRemovalNamesOnlyAMissingLogFolderThatHeldItsRowsAsync()
    {
        using var ctx = new PhaseContext();
        await using var database = await TestDatabase.CreateAsync();
        await using var context = new AppDbContext(database.Options);
        context.Downloads.Add(new Download
        {
            Service = PrefillPlatform.Steam.ToService(), ClientIp = "127.0.0.1", Datasource = "other", GameAppId = 123,
            GameName = "Removed", IsEvicted = true, StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var source = Assert.Single(ctx.Datasources.GetDatasources());
        source.SchemeOverride = DatasourceSchemeOverride.Monolithic;
        Directory.Delete(source.LogPath, recursive: true);

        await ctx.WaitForRepairAsync(
            ctx.Scan.RemoveEvictedRecordsAsync(context, CancellationToken.None),
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => ctx._operationStateService.GetBlockingRepair() is null);

        var removal = Assert.Single(tracker.GetRuns().Runs,
            run => run.OperationType == OperationType.EvictionRemoval.ToWireString());
        Assert.Empty(removal.Warnings);
        Assert.False(removal.Retained);
        Assert.False(await context.Downloads.AnyAsync(download => download.IsEvicted));
    }

    [Fact]
    public async Task ACanceledEvictionRemovalDoesNotSayItRemovedTheDownloadsAsync()
    {
        using var ctx = new PhaseContext();
        await using var database = await TestDatabase.CreateAsync();
        await using var context = new AppDbContext(database.Options);
        context.Downloads.Add(new Download
        {
            Service = PrefillPlatform.Steam.ToService(), ClientIp = "127.0.0.1", Datasource = "Default", GameAppId = 123,
            GameName = "Removed", IsEvicted = true, StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var source = Assert.Single(ctx.Datasources.GetDatasources());
        source.SchemeOverride = DatasourceSchemeOverride.Monolithic;
        Directory.Delete(source.LogPath, recursive: true);
        var removalId = Guid.Empty;
        ctx.Notifications.OnSent = (eventName, value) =>
        {
            if (eventName == SignalREvents.EvictionRemovalStarted && value is EvictionRemovalStarted started)
            {
                removalId = started.OperationId;
            }
            // The first progress report comes inside the deletes' transaction, after the step has
            // looked at the log folders, so the cancel lands between the two.
            else if (eventName == SignalREvents.EvictionRemovalProgress)
            {
                tracker.CancelOperation(removalId);
            }
        };

        await ctx.WaitForRepairAsync(
            ctx.Scan.RemoveEvictedRecordsAsync(context, CancellationToken.None),
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => ctx._operationStateService.GetBlockingRepair() is null);

        var removal = Assert.Single(tracker.GetRuns().Runs,
            run => run.OperationType == OperationType.EvictionRemoval.ToWireString());
        Assert.Equal(OperationStatus.Cancelled.ToWireString(), removal.Status);
        Assert.Empty(removal.Warnings);
        Assert.True(await context.Downloads.AnyAsync(download => download.IsEvicted));
    }

    [Fact]
    public async Task SaveRemoveRetriesReopenBeforeDeletingEvictedRowsAsync()
    {
        using var ctx = new PhaseContext();
        await using var database = await TestDatabase.CreateAsync();
        var source = Assert.Single(ctx.Datasources.GetDatasources());
        var target = Path.Combine(source.LogPath, "access.log");
        await File.WriteAllLinesAsync(target,
        [
            "GET /remove HTTP/1.1",
            "GET /keep HTTP/1.1"
        ]);

        await using (var seed = new AppDbContext(database.Options))
        {
            var removed = new Download
            {
                Service = "steam",
                ClientIp = "127.0.0.1",
                Datasource = source.Name,
                GameAppId = 123,
                GameName = "Removed",
                CacheHitBytes = 1,
                IsEvicted = true,
                StartTimeUtc = DateTime.UtcNow,
                EndTimeUtc = DateTime.UtcNow
            };
            var kept = new Download
            {
                Service = "steam",
                ClientIp = "127.0.0.2",
                Datasource = source.Name,
                GameAppId = 456,
                GameName = "Kept",
                CacheHitBytes = 1,
                IsEvicted = false,
                StartTimeUtc = DateTime.UtcNow,
                EndTimeUtc = DateTime.UtcNow
            };
            seed.Downloads.AddRange(removed, kept);
            await seed.SaveChangesAsync();
            seed.LogEntries.AddRange(
                new LogEntryRecord
                {
                    Timestamp = DateTime.UtcNow,
                    ClientIp = removed.ClientIp,
                    Service = removed.Service,
                    Method = "GET",
                    Url = "/remove",
                    StatusCode = 200,
                    Datasource = source.Name,
                    DownloadId = removed.Id
                },
                new LogEntryRecord
                {
                    Timestamp = DateTime.UtcNow,
                    ClientIp = kept.ClientIp,
                    Service = kept.Service,
                    Method = "GET",
                    Url = "/keep",
                    StatusCode = 200,
                    Datasource = source.Name,
                    DownloadId = kept.Id
                });
            seed.CachedGameDetections.AddRange(
                new CachedGameDetection
                {
                    GameAppId = 123,
                    GameName = "Removed",
                    Service = "steam",
                    IsEvicted = true
                },
                new CachedGameDetection
                {
                    GameAppId = 456,
                    GameName = "Kept",
                    Service = "steam",
                    IsEvicted = false
                });
            seed.PrefillCachedApps.AddRange(
                new PrefillCachedApp
                {
                    Platform = PrefillPlatform.Steam,
                    AppId = "123",
                    AppName = "Removed",
                    CachedAtUtc = DateTime.UtcNow
                },
                new PrefillCachedApp
                {
                    Platform = PrefillPlatform.Steam,
                    AppId = "456",
                    AppName = "Kept",
                    CachedAtUtc = DateTime.UtcNow
                });
            await seed.SaveChangesAsync();
        }

        ctx.State.SetLogSourcePositions(source.Name, new Dictionary<string, long> { ["access"] = 10 });
        ctx.State.SetLogTotalLines(source.Name, 20);
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(
            processManager,
            NullLogger<UnifiedOperationTracker>.Instance);
        var paths = new TempDirPathResolver(ctx.Root) { DockerSocketAvailable = true };
        var rust = new SaveRustProcessHelper(target, paths, tracker);
        var nginx = new SaveNginxLogRotationService(
            source.LogPath,
            paths,
            reopenAnswers: new Queue<ProcessCommandResult>(
            [
                new ProcessCommandResult { ExitCode = 41, Error = "first reopen denied" },
                new ProcessCommandResult { ExitCode = 0 }
            ]));
        var capability = new DatasourceCapabilityService(ctx.Datasources);
        var gate = Idle();
        var configuration = new ConfigurationBuilder().Build();
        OperationStateService operationState = null!;
        CacheReconciliationService repairService = null!;
        RustLogProcessorService logProcessor = null!;
        CacheManagementService cacheManagement = null!;
        CorruptionDetectionService corruptionDetection = null!;
        var registrations = new ServiceCollection();
        registrations.AddScoped(_ => new AppDbContext(database.Options));
        registrations.AddSingleton(_ => operationState!);
        registrations.AddSingleton(_ => repairService!);
        registrations.AddSingleton(_ => logProcessor!);
        registrations.AddSingleton(_ => cacheManagement!);
        registrations.AddSingleton(_ => corruptionDetection!);
        registrations.AddSingleton(ctx.Datasources);
        registrations.AddSingleton(capability);
        registrations.AddSingleton((ISignalRNotificationService)(object)ctx.Notifications);
        using var services = registrations.BuildServiceProvider();
        var lifetime = new TestHostApplicationLifetime();
        var repairLog = new CapturingLogger<OperationStateService>();
        var purgeLog = new CapturingLogger<CacheReconciliationService>();
        operationState = new OperationStateService(
            repairLog,
            configuration,
            ctx.State,
            services.GetRequiredService<IServiceScopeFactory>(),
            lifetime,
            processManager,
            tracker);
        await operationState.StartAsync(CancellationToken.None);
        var detectionStore = new GameCacheDetectionDataService(
            database.Factory,
            NullLogger<GameCacheDetectionDataService>.Instance);
        var detection = new GameCacheDetectionService(
            NullLogger<GameCacheDetectionService>.Instance,
            paths,
            operationState,
            database.Factory,
            detectionStore,
            evictedDetectionPreservationService: null!,
            unknownGameResolutionService: null!,
            rust,
            (ISignalRNotificationService)(object)ctx.Notifications,
            ctx.Datasources,
            capability,
            tracker,
            gate);
        var conflicts = new OperationConflictChecker(
            tracker,
            operationState,
            NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationQueueService(
            tracker,
            conflicts,
            NullLogger<OperationQueueService>.Instance);
        logProcessor = new RustLogProcessorService(
            NullLogger<RustLogProcessorService>.Instance,
            paths,
            (ISignalRNotificationService)(object)ctx.Notifications,
            ctx.State,
            services,
            rust,
            ctx.Datasources,
            tracker);
        corruptionDetection = new CorruptionDetectionService(
            NullLogger<CorruptionDetectionService>.Instance,
            configuration,
            paths,
            rust,
            (ISignalRNotificationService)(object)ctx.Notifications,
            ctx.Datasources,
            database.Factory,
            operationState,
            tracker,
            capability,
            gate,
            nginxLogRotationService: null!,
            stateService: null!);
        cacheManagement = new CacheManagementService(
            configuration,
            NullLogger<CacheManagementService>.Instance,
            paths,
            rust,
            nginx,
            ctx.Datasources,
            ctx.State,
            database.Factory,
            detection,
            tracker,
            (ISignalRNotificationService)(object)ctx.Notifications,
            envFileReader: null!,
            conflicts,
            capability,
            gate,
            operationState);
        var reconciliation = new CacheReconciliationService(
            services,
            purgeLog,
            configuration,
            ctx.Datasources,
            ctx.State,
            (ISignalRNotificationService)(object)ctx.Notifications,
            tracker,
            rust,
            nginx,
            paths,
            detectionStore,
            detection,
            evictedDetectionPreservationService: null!,
            queue,
            new TestHostApplicationLifetime(),
            capability,
            gate);
        repairService = reconciliation;
        await using var requestContext = new AppDbContext(database.Options);
        var speedTracker = new RustSpeedTrackerService(
            NullLogger<RustSpeedTrackerService>.Instance,
            configuration,
            paths,
            ctx.Datasources,
            (ISignalRNotificationService)(object)ctx.Notifications,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            capability,
            ctx.State,
            operationState);
        var controller = new StatsController(
            requestContext,
            clientGroupsRepository: null!,
            ctx.State,
            Options.Create(new ApiOptions()),
            (ISignalRNotificationService)(object)ctx.Notifications,
            reconciliation,
            tracker,
            conflicts,
            queue,
            capability,
            clientHostnameService: null!,
            eventsService: null!,
            speedTracker,
            operationState)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var rustPath = paths.GetRustLogPurgePath();
        var rustMarker = "save-remove-" + Guid.NewGuid().ToString("N");
        var createdRustMarker = !File.Exists(rustPath);
        if (createdRustMarker)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(rustPath)!);
            await File.WriteAllTextAsync(rustPath, rustMarker);
        }

        try
        {
            async Task<Guid> SaveAsync()
            {
                var response = await controller.UpdateEvictionSettingsAsync(new UpdateEvictionSettingsRequest
                {
                    EvictedDataMode = EvictedDataMode.Remove.ToWireString()
                });
                var accepted = Assert.IsType<AcceptedResult>(response.Result);
                Assert.Equal(StatusCodes.Status202Accepted, accepted.StatusCode);
                var operationId = Assert.IsType<Guid>(
                    accepted.Value!.GetType().GetProperty("operationId")!.GetValue(accepted.Value));
                Assert.NotEqual(Guid.Empty, operationId);
                Assert.Equal(EvictedDataMode.Remove.ToWireString(), ctx.State.GetEvictedDataMode());
                return operationId;
            }

            var firstLogStart = purgeLog.Entries.Count;
            var purgeWarnings = () => purgeLog.Entries
                .Skip(firstLogStart)
                .Where(entry => entry.Level == LogLevel.Warning && entry.Exception is not null)
                .ToList();
            var firstId = await SaveAsync();
            var first = await WaitForTerminalAsync(tracker, firstId, RepairErrors);
            Assert.Equal(OperationStatus.Failed, first.Status);

            // The removal's log step started, so its repair resets the positions and redoes the
            // whole step; the evicted rows stay until a reopen has succeeded.
            await nginx.SecondReopenReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var firstWarning = Assert.Single(purgeWarnings());
            var firstError = Assert.IsType<InvalidOperationException>(firstWarning.Exception);
            Assert.Contains("first reopen denied", firstError.Message, StringComparison.Ordinal);
            Assert.True(rust.Runs == 2, first.Message);
            Assert.True(rust.PublicationCount == 2, rust.Arguments);
            // Each run's published identity differs from the file it replaced (asserted in the fake child);
            // not compared with the first file here, because Linux can reuse its inode for the second one.
            Assert.DoesNotContain("/remove", await File.ReadAllTextAsync(target), StringComparison.Ordinal);
            Assert.Contains("/keep", await File.ReadAllTextAsync(target), StringComparison.Ordinal);
            Assert.Empty(ctx.State.GetLogSourcePositions(source.Name));
            Assert.Equal(0, ctx.State.GetLogTotalLines(source.Name));
            await AssertRemovalRowsAsync(database, removed: false);

            nginx.ReleaseSecondReopen.SetResult();
            await WaitUntilAsync(() => ctx.State.LoadOperationRepairs()
                .Any(repair => repair.Id == firstId && repair.Phase == OperationRepairPhase.Completed));
            Assert.Single(purgeWarnings());
            var kept = Assert.Single(ctx.State.LoadOperationRepairs().Single(repair => repair.Id == firstId).Sources);
            Assert.True(kept.LogPositionsKept);
            await AssertRemovalRowsAsync(database, removed: true);
            Assert.Empty(ctx.State.GetLogSourcePositions(source.Name));
            Assert.Equal(0, ctx.State.GetLogTotalLines(source.Name));
            Assert.Equal(2, rust.Runs);
            Assert.Equal(2, rust.PublicationCount);
            Assert.Equal(2, nginx.SignalCalls);
            Assert.Equal(1, ctx.Notifications.Count(
                SignalREvents.EvictionRemovalStarted,
                value => value is EvictionRemovalStarted started && started.OperationId == firstId));
            Assert.Equal(1, ctx.Notifications.Count(
                SignalREvents.EvictionRemovalComplete,
                value => value is EvictionRemovalComplete complete && complete.OperationId == firstId));
            Assert.Equal(0, ctx.Notifications.Count(SignalREvents.EvictionScanStarted));

            string RepairErrors() => string.Join(
                Environment.NewLine,
                repairLog.Entries
                    .Where(entry => entry.Level >= LogLevel.Error)
                    .Select(entry => $"{entry.Message}: {entry.Exception}"));
        }
        finally
        {
            lifetime.StopApplication();
            await operationState.StopAsync(CancellationToken.None);
            if (createdRustMarker && File.Exists(rustPath))
            {
                Assert.Equal(rustMarker, await File.ReadAllTextAsync(rustPath));
                File.Delete(rustPath);
            }
        }
    }

    [Fact]
    public async Task ThePhaseStartsAHiddenFullDetectionForwardsItsPercentAndWaitsForItsTerminal()
    {
        using var ctx = new PhaseContext();
        var scanId = ctx.RegisterScan();

        var phase = ctx.RunPhaseAsync(scanId, CancellationToken.None);

        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        var metrics = Assert.IsType<GameDetectionMetrics>(detection.Metadata);
        Assert.Equal(NotificationMode.Hidden, detection.Notice!.Mode);
        Assert.Equal(RunTrigger.Manual, detection.Notice.Trigger);
        Assert.Equal(DetectionScanType.Full, metrics.ScanType);
        Assert.Equal(scanId, detection.ParentOperationId);
        Assert.Equal(scanId, metrics.ParentOperationId);
        Assert.NotEqual(scanId, detection.Id);

        ctx.Tracker.SetPercent(detection.Id, 42);
        var forwarded = await ctx.Notifications.WaitForAsync(
            SignalREvents.EvictionScanProgress,
            payload => payload is EvictionScanProgress { StageKey: "signalr.evictionScan.detectingGames", PercentComplete: 42 });
        Assert.Equal(scanId, Assert.IsType<EvictionScanProgress>(forwarded).OperationId);
        Assert.False(phase.IsCompleted);

        await ctx.CompleteDetectionAsync(detection.Id);

        await phase.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(ctx.Tracker.Waiting);
    }

    [Fact]
    public async Task PromotedScanReusesTheCompletedFullDetectionThatBlockedIt()
    {
        using var ctx = new PhaseContext();
        var detectionId = Guid.NewGuid();
        ctx.Detection.RememberCompletedDetection(detectionId, DetectionScanType.Full, success: true, cancelled: false);
        var notice = new RunNotice(NotificationMode.Hidden, RunTrigger.Manual) { BlockedByOperationId = detectionId };
        var scanId = ctx.RegisterScan(notice: notice);

        await ctx.RunPhaseAsync(scanId, CancellationToken.None);

        Assert.Equal(0, ctx.Tracker.Count(OperationType.GameDetection));
    }

    [Fact]
    public async Task InvalidatedDetectionStillStartsAHiddenFullScan()
    {
        using var ctx = new PhaseContext();
        var detectionId = Guid.NewGuid();
        ctx.Detection.RememberCompletedDetection(detectionId, DetectionScanType.Full, success: true, cancelled: false);
        ctx.Detection.InvalidateDetectionCache();
        var notice = new RunNotice(NotificationMode.Hidden, RunTrigger.Manual) { BlockedByOperationId = detectionId };
        var scanId = ctx.RegisterScan(notice: notice);

        var phase = ctx.RunPhaseAsync(scanId, CancellationToken.None);
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        Assert.NotEqual(detectionId, detection.Id);
        await ctx.CompleteDetectionAsync(detection.Id);
        await phase.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void OnlyASuccessfulFullDetectionStaysReusable()
    {
        using var ctx = new PhaseContext();
        var detectionId = Guid.NewGuid();
        ctx.Detection.RememberCompletedDetection(detectionId, DetectionScanType.Incremental, success: true, cancelled: false);
        ctx.Detection.RememberCompletedDetection(detectionId, DetectionScanType.Full, success: false, cancelled: false);
        ctx.Detection.RememberCompletedDetection(detectionId, DetectionScanType.Full, success: true, cancelled: true);
        Assert.False(ctx.Detection.HasReusableFullDetection(detectionId));

        ctx.Detection.RememberCompletedDetection(detectionId, DetectionScanType.Full, success: true, cancelled: false);
        Assert.True(ctx.Detection.HasReusableFullDetection(detectionId));
        ctx.Detection.InvalidateDetectionCache();
        Assert.False(ctx.Detection.HasReusableFullDetection(detectionId));
    }

    [Fact]
    public async Task PromotionRecordsTheDetectionThatBlockedTheScan()
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var detectionId = tracker.RegisterOperation(
            OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            new GameDetectionMetrics { ScanType = DetectionScanType.Full });
        var queue = new OperationQueueService(tracker,
            new OperationConflictChecker(
                tracker,
                ctx._operationStateService,
                NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var started = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = await queue.EnqueueAsync(
            OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan",
            () =>
            {
                started.TrySetResult(notice.BlockedByOperationId);
                return Task.FromResult<Guid?>(Guid.NewGuid());
            },
            CancellationToken.None,
            notice: notice);

        Assert.True(queued.Queued);
        tracker.CompleteOperation(detectionId, success: true);
        Assert.Equal(detectionId, await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PromotionLeavesTheQueuedScanRunningWhenThatScanKeepsItsId()
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var detectionId = tracker.RegisterOperation(
            OperationType.GameDetection, "Game Detection", new CancellationTokenSource());
        var queue = new OperationQueueService(tracker,
            new OperationConflictChecker(
                tracker,
                ctx._operationStateService,
                NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationQueueService>.Instance);
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = await queue.EnqueueAsync(
            OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan",
            () =>
            {
                var parkedId = notice.OperationId!.Value;
                Assert.True(tracker.BeginQueuedOperation(parkedId, new Dictionary<string, object?>(), null, null));
                continued.TrySetResult();
                return Task.FromResult<Guid?>(parkedId);
            },
            CancellationToken.None,
            notice: notice);

        Assert.True(queued.Queued);
        tracker.CompleteOperation(detectionId, success: true);
        await continued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(queued.OperationId)!.Status);
    }

    [Fact]
    public void ARunningScanKeepsItsTerminalWhenItsQueuedTokenIsCancelled()
    {
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var token = new CancellationTokenSource();
        var parkedId = tracker.RegisterOperation(
            OperationType.EvictionScan, "Eviction Scan", token,
            metadata: new Dictionary<string, object?> { ["waiting"] = true },
            initialStatus: OperationStatus.Waiting);

        // Still parked: the queue owns the terminal because no worker exists.
        Assert.True(tracker.BeginQueuedOperation(parkedId, new Dictionary<string, object?>(), null, null));
        Assert.False(tracker.CancelParkedOperation(parkedId));
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(parkedId)!.Status);

        tracker.CompleteOperation(parkedId, success: true);
        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(parkedId)!.Status);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task CancellingAHeldOrAcknowledgedScanLeavesARunningScansTerminalToItsWorker(bool acknowledged, bool begun)
    {
        const string evictionKey = "cacheReconciliation";
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        using var loop = new EvictionLoopProbe();
        var schedules = new ServiceScheduleRegistry([loop], VisibleClientsStateService(),
            DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>(),
            ScheduleExecutionTestService.Create(), tracker);
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var placed = typeof(ServiceScheduleRegistry)
            .GetMethod(acknowledged ? "AcknowledgeRun" : "HoldRefusedRunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(schedules, acknowledged
                ? [loop, evictionKey, OperationType.EvictionScan, notice]
                : [evictionKey, OperationType.EvictionScan, notice]);
        if (placed is Task holding) await holding;
        var heldId = notice.PendingId!.Value;
        var terminal = new TaskCompletionSource<OperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.OperationTerminal += operation =>
        {
            if (operation.Id == heldId) terminal.TrySetResult(operation.Status);
        };

        // The scan's worker continues the held operation in place, the way RegisterEvictionScanOperation does.
        if (begun)
        {
            Assert.True(tracker.BeginQueuedOperation(heldId, new Dictionary<string, object?>(), null, null));
        }

        Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(heldId));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((await loop.ReadRunStatusAsync()).HeldCard is not null)
        {
            Assert.True(DateTime.UtcNow < deadline, "The cancel callback did not drop the hold");
            await Task.Delay(10);
        }

        if (!begun)
        {
            // No worker exists, so the callback is what ends it.
            Assert.Equal(OperationStatus.Cancelled, await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            return;
        }

        // The callback runs on the thread pool; an early terminal lands well inside this window.
        Assert.NotSame(terminal.Task, await Task.WhenAny(terminal.Task, Task.Delay(TimeSpan.FromMilliseconds(500))));
        Assert.Equal(OperationStatus.Cancelling, tracker.GetOperation(heldId)!.Status);
        // Still active, so the conflict check keeps every other heavy operation waiting.
        Assert.Contains(heldId, tracker.GetActiveOperations(OperationType.EvictionScan).Select(operation => operation.Id));

        tracker.CompleteOperation(heldId, success: false, cancelled: true);
        Assert.Equal(OperationStatus.Cancelled, await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class EvictionLoopProbe() : LancacheManager.Infrastructure.Services.Base.ScheduledBackgroundService(
        NullLogger<EvictionLoopProbe>.Instance, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
    {
        public override string ServiceKey => "cacheReconciliation";
        protected override string ServiceName => "cacheReconciliation";
        protected override TimeSpan Interval => TimeSpan.FromHours(1);
        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    [Fact]
    public void AParkedScanIsCancelledByTheQueueAndRefusesToStart()
    {
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var parkedId = tracker.RegisterOperation(
            OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
            metadata: new Dictionary<string, object?> { ["waiting"] = true },
            initialStatus: OperationStatus.Waiting);

        Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(parkedId));
        // The cancel moved it to Cancelling without giving it a worker, so it is still the queue's.
        Assert.Contains(parkedId, tracker.GetWaitingOperations().Select(operation => operation.Id));
        Assert.False(tracker.BeginQueuedOperation(parkedId, new Dictionary<string, object?>(), null, null));
        Assert.True(tracker.CancelParkedOperation(parkedId));
        Assert.Equal(OperationStatus.Cancelled, tracker.GetOperation(parkedId)!.Status);
        Assert.False(tracker.CancelParkedOperation(parkedId));
    }

    [Fact]
    public void AQueuedEvictionScanContinuesTheParkedOperation()
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var parkedToken = new CancellationTokenSource();
        var parkedId = tracker.RegisterOperation(
            OperationType.EvictionScan, "Eviction Scan", parkedToken, initialStatus: OperationStatus.Waiting);
        var notice = new RunNotice(NotificationMode.Hidden, RunTrigger.Manual);
        notice.Attach(tracker, parkedId);

        var continued = (Guid)typeof(CacheReconciliationService).GetMethod(
            "RegisterEvictionScanOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(
            ctx.Scan, ["Eviction Scan", parkedToken, notice])!;

        Assert.Equal(parkedId, continued);
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(parkedId)!.Status);
        Assert.True(tracker.GetOperation(parkedId)!.OwnerCompletes);
        Assert.Equal(parkedId, Assert.Single(tracker.GetActiveOperations(OperationType.EvictionScan)).Id);
    }

    [Fact]
    public async Task RepairScanKeepsTrackerAndCheckpointIdentitiesSeparate()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "lcm-repair-scan-" + Guid.NewGuid().ToString("N"));
        var paths = new TempDirPathResolver(root);
        var binary = paths.GetRustEvictionScanPath();
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        await File.WriteAllTextAsync(binary, string.Empty);
        var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(
            processes,
            NullLogger<UnifiedOperationTracker>.Instance);
        var rust = new CapturingEvictionRust(processes, paths, tracker);
        var originalId = Guid.NewGuid();
        var checkpointId = Guid.NewGuid();
        var repairPath = Path.Combine(root, "repair.json");

        var result = await rust.RunEvictionScanAsync(
            Path.Combine(root, "datasources.json"),
            Path.Combine(root, "progress.json"),
            CancellationToken.None,
            originalId,
            onProgressEvent: null,
            scanId: checkpointId,
            repairPath: repairPath);

        Assert.True(result.Success);
        Assert.Equal(originalId, rust.TrackedOperationId);
        Assert.Contains($"--operation-id \"{checkpointId}\"", rust.Arguments, StringComparison.Ordinal);
        Assert.Contains($"--repair \"{repairPath}\"", rust.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain(originalId.ToString(), rust.Arguments, StringComparison.OrdinalIgnoreCase);
        var missingCheckpoint = await rust.RunEvictionScanAsync(
            Path.Combine(root, "datasources.json"),
            operationId: originalId,
            repairPath: repairPath);
        Assert.False(missingCheckpoint.Success);
        Assert.NotNull(missingCheckpoint.Error);
        Assert.Contains("scan ID is required", missingCheckpoint.Error, StringComparison.OrdinalIgnoreCase);

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizedInternalCheckpointDoesNotStartAnotherScan()
    {
        var databaseName = "lcm-finalized-checkpoint-" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        var registrations = new ServiceCollection();
        registrations.AddScoped(_ => new AppDbContext(options));
        using var services = registrations.BuildServiceProvider();
        var checkpointId = Guid.NewGuid();
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.EvictionScanCheckpoints.Add(new EvictionScanCheckpoint
            {
                OperationId = checkpointId,
                Processed = 7,
                Evicted = 3,
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                FinalizedAtUtc = DateTime.UtcNow.AddMinutes(-1)
            });
            await context.SaveChangesAsync();
        }

        var scan = (CacheReconciliationService)RuntimeHelpers.GetUninitializedObject(
            typeof(CacheReconciliationService));
        PhaseContext.SetField(scan, "_serviceProvider", services);
        await scan.ReconcileRepairAsync(
            new OperationRepair
            {
                Id = Guid.NewGuid(),
                Type = OperationType.CacheClearing,
                Name = "Cache Clearing",
                StartedAt = DateTime.UtcNow.AddMinutes(-3),
                RetryAtUtc = DateTime.UtcNow.AddSeconds(-1),
                EvictionScanId = checkpointId,
                CacheClearing = new CacheClearingRepair { EntityKey = "all" },
                Sources =
                [
                    new OperationRepairSource
                    {
                        Datasource = "default",
                        CacheRoot = "C:/cache",
                        KeyScheme = "monolithic",
                        NativeLaunchAuthorized = true,
                        ReconcileCache = true
                    }
                ]
            },
            CancellationToken.None);

        await using var verify = new AppDbContext(options);
        Assert.Single(await verify.EvictionScanCheckpoints.ToListAsync());
    }

    [Fact]
    public async Task CancellingTheScanCancelsTheDetectionItIsWaitingOn()
    {
        using var ctx = new PhaseContext();
        using var cts = new CancellationTokenSource();

        var scanId = ctx.RegisterScan();
        var phase = ctx.RunPhaseAsync(scanId, cts.Token);
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);

        cts.Cancel();

        await phase.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(detection.Id, ctx.Tracker.Cancelled);
        Assert.Empty(ctx.Tracker.Waiting);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectionFailureRemainsOnTheRunningParentThroughLaterProgress(bool silent)
    {
        using var ctx = new PhaseContext();
        var scanId = ctx.RegisterScan(new RunNotice(silent ? NotificationMode.Silent : NotificationMode.All, RunTrigger.Scheduled));
        var phase = ctx.RunPhaseAsync(scanId, CancellationToken.None);
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        ctx.Tracker.Fail(detection.Id, "Cache index could not be read");
        await phase.WaitAsync(TimeSpan.FromSeconds(5));
        await ctx.ReportProgressAsync(scanId);
        var progress = Assert.IsType<EvictionScanProgress>(await ctx.Notifications.WaitForAsync(
            SignalREvents.EvictionScanProgress, value => value is EvictionScanProgress { StageKey: "signalr.evictionScan.scanning" }));
        Assert.Equal("Cache index could not be read", progress.Context!["detectionError"]);
        Assert.Equal("Cache index could not be read", ctx.Scan.CurrentScanProgressContext!["detectionError"]);
        Assert.Equal(OperationStatus.Running, ctx.Tracker.Get(scanId)!.Status);
    }

    [Fact]
    public async Task SilentParentTerminalKeepsDetectionDetailAndRejectsLaterProgress()
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var id = ctx.RegisterScan(new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled));
        var holders = typeof(CacheReconciliationService).GetField("_evictionScanTerminalStates", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(ctx.Scan)!;
        var holder = holders.GetType().GetProperty("Item")!.GetValue(holders, [id])!;
        tracker.UpdateMetadata(id, values => holder.GetType().GetField("DetectionError")!.SetValue(holder, "Cache index could not be read"));
        await ctx.ReportProgressAsync(id);
        tracker.CompleteOperation(id, success: true);
        var terminal = Assert.IsType<EvictionScanComplete>(await ctx.Notifications.WaitForAsync(
            SignalREvents.EvictionScanComplete, value => value is EvictionScanComplete { OperationId: var operationId } && operationId == id));
        Assert.True(terminal.Success);
        Assert.Null(terminal.Error);
        Assert.Equal("Cache index could not be read", terminal.Context!["detectionError"]);
        // The silent run still ends as a kept warning, so the failed detection is not lost.
        var row = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == id);
        Assert.Equal("Cache index could not be read", Assert.Single(row.Warnings).Context["errorDetail"]);
        Assert.True(row.Retained);
        var percent = tracker.GetOperation(id)!.PercentComplete;
        await ctx.ReportProgressAsync(id);
        Assert.Equal(percent, tracker.GetOperation(id)!.PercentComplete);
        Assert.Null(ctx.Scan.CurrentScanProgressContext);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AScanWhoseDetectionFailedEndsAsAKeptWarningCarryingTheError(bool detectionFailed)
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var id = ctx.RegisterScan();
        if (detectionFailed)
        {
            // What the detection phase records on the scan when its child ends Failed.
            var holders = typeof(CacheReconciliationService).GetField("_evictionScanTerminalStates", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(ctx.Scan)!;
            var holder = holders.GetType().GetProperty("Item")!.GetValue(holders, [id])!;
            tracker.UpdateMetadata(id, _ => holder.GetType().GetField("DetectionError")!.SetValue(holder, "Cache index could not be read"));
        }

        await ctx.ReportProgressAsync(id);
        tracker.CompleteOperation(id, success: true);

        var row = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == id);
        Assert.Equal(detectionFailed ? "Cache index could not be read" : null, row.Warnings.SingleOrDefault()?.Context["errorDetail"]);
        Assert.Equal(detectionFailed, row.Retained);
    }

    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData(NotificationMode.All, RunTrigger.Scheduled, RunVisibility.Card)]
    [InlineData(NotificationMode.Manual, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.Manual, RunTrigger.RunAll, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Manual, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Manual, RunVisibility.Hidden)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Scheduled, RunVisibility.Hidden)]
    public async Task TheScansOwnCleanupFollowsTheScansModeAndTrigger(
        NotificationMode mode, RunTrigger trigger, RunVisibility expected)
    {
        using var ctx = new PhaseContext();
        await using var context = ctx.CreateContext();
        context.Downloads.Add(new Download
        {
            Service = PrefillPlatform.Steam.ToService(), ClientIp = "127.0.0.1", Datasource = "Default", GameAppId = 123,
            GameName = "Removed", IsEvicted = true, StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        // What the scan reads on its way to its Remove-mode cleanup. The native stand-in marks
        // the scan checkpoint finalized so this test can isolate parent-to-child notice transfer.
        ctx.State.SetEvictedDataMode(EvictedDataMode.Remove.ToWireString());
        PhaseContext.SetField(ctx.Scan, "_stateService", ctx.State);
        PhaseContext.SetField(ctx.Scan, "_datasourceService", ctx.Datasources);
        PhaseContext.SetField(ctx.Scan, "_cacheScanGate", Idle());
        PhaseContext.SetField(ctx.Scan, "_rustProcessHelper", new ScanResultRustProcessHelper(
            async (operationId, cancellationToken) =>
            {
                var checkpoint = await context.EvictionScanCheckpoints
                    .SingleAsync(item => item.OperationId == operationId, cancellationToken);
                checkpoint.FinalizedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
            }));
        var scanNotice = new RunNotice(mode, trigger);
        var scanId = ctx.RegisterScan(scanNotice);
        RunVisibility? whileRunning = null;
        Guid removalId = default;
        ctx.Notifications.OnSent = (eventName, value) =>
        {
            if (eventName == SignalREvents.EvictionRemovalStarted && value is EvictionRemovalStarted started)
            {
                removalId = started.OperationId;
                Assert.Equal(OperationStatus.Running, tracker.GetOperation(scanId)!.Status);
                whileRunning = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == started.OperationId).Visibility;
            }
        };

        var scan = (Task)typeof(CacheReconciliationService)
            .GetMethod("ReconcileCacheFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Scan, [context, scanId, CancellationToken.None, scanNotice])!;
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        await ctx.CompleteDetectionAsync(detection.Id);
        await ctx.WaitForRepairAsync(scan, TimeSpan.FromSeconds(10));

        Assert.Equal(expected, whileRunning);
        // A fresh notice with the scan's mode and trigger; the scan's own notice stays with the scan.
        var cleanupNotice = tracker.GetOperation(removalId)!.Notice!;
        Assert.NotSame(scanNotice, cleanupNotice);
        Assert.Equal(scanNotice.Mode, cleanupNotice.Mode);
        Assert.Equal(scanNotice.Trigger, cleanupNotice.Trigger);
        // The fixture has no summary service, so the cleanup fails after its commit; a failure stays
        // until closed whatever the mode.
        var ended = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == removalId);
        Assert.Equal(OperationStatus.Failed.ToWireString(), ended.Status);
        Assert.True(ended.Retained);
    }

    [Fact]
    public async Task AStopDuringTheRemoveStepIsKeptForTheRestartAsync()
    {
        using var ctx = new PhaseContext();
        await using var context = ctx.CreateContext();
        context.Downloads.Add(new Download
        {
            Service = PrefillPlatform.Steam.ToService(), ClientIp = "127.0.0.1", Datasource = "Default", GameAppId = 123,
            GameName = "Removed", IsEvicted = true, StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        ctx.State.SetEvictedDataMode(EvictedDataMode.Remove.ToWireString());
        PhaseContext.SetField(ctx.Scan, "_stateService", ctx.State);
        PhaseContext.SetField(ctx.Scan, "_datasourceService", ctx.Datasources);
        PhaseContext.SetField(ctx.Scan, "_cacheScanGate", Idle());
        PhaseContext.SetField(ctx.Scan, "_rustProcessHelper", new ScanResultRustProcessHelper(
            async (operationId, cancellationToken) =>
            {
                var checkpoint = await context.EvictionScanCheckpoints
                    .SingleAsync(item => item.OperationId == operationId, cancellationToken);
                checkpoint.FinalizedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
            }));
        var scanNotice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var scanId = ctx.RegisterScan(scanNotice);
        // The token the scan's worker passes (X on the scan's card cancels it); the removal's own token is linked to it.
        using var stop = new CancellationTokenSource();
        ctx.Notifications.OnSent = (eventName, _) =>
        {
            if (eventName == SignalREvents.EvictionRemovalStarted)
            {
                stop.Cancel();
            }
        };

        var scan = (Task)typeof(CacheReconciliationService)
            .GetMethod("ReconcileCacheFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Scan, [context, scanId, stop.Token, scanNotice])!;
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        await ctx.CompleteDetectionAsync(detection.Id);
        await ctx.WaitForRepairAsync(scan, TimeSpan.FromSeconds(10));

        var outcome = scan.GetType().GetProperty("Result")!.GetValue(scan)!;
        Assert.True((bool)outcome.GetType().GetProperty("Cancelled")!.GetValue(outcome)!);
        // The scan saved its own outcome before the remove step; the record keeps that the person stopped the run,
        // so a restart restores the gray card the session showed.
        var record = ctx.State.LoadOperationRepairs().Single(repair => repair.Id == scanId);
        Assert.Equal(OperationStatus.Completed, record.Outcome);
        Assert.True(record.RunCancelled);
    }

    [Fact]
    public async Task AForceStopDuringTheRemoveStepEndsTheScanAtOnceAsync()
    {
        using var ctx = new PhaseContext();
        await using var context = ctx.CreateContext();
        context.Downloads.Add(new Download
        {
            Service = PrefillPlatform.Steam.ToService(), ClientIp = "127.0.0.1", Datasource = "Default", GameAppId = 123,
            GameName = "Removed", IsEvicted = true, StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        ctx.State.SetEvictedDataMode(EvictedDataMode.Remove.ToWireString());
        PhaseContext.SetField(ctx.Scan, "_stateService", ctx.State);
        PhaseContext.SetField(ctx.Scan, "_datasourceService", ctx.Datasources);
        PhaseContext.SetField(ctx.Scan, "_cacheScanGate", Idle());
        PhaseContext.SetField(ctx.Scan, "_rustProcessHelper", new ScanResultRustProcessHelper(
            async (operationId, cancellationToken) =>
            {
                var checkpoint = await context.EvictionScanCheckpoints
                    .SingleAsync(item => item.OperationId == operationId, cancellationToken);
                checkpoint.FinalizedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
            }));
        var scanNotice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var scanId = ctx.RegisterScan(scanNotice);
        var cancellation = new OperationCancellationService(
            tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            ctx._operationStateService,
            NullLogger<OperationCancellationService>.Instance);
        // The token the scan's worker passes (X on the scan's card cancels it); the removal's own token is linked to it.
        using var stop = new CancellationTokenSource();
        var removeStepStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Notifications.OnSent = (eventName, _) =>
        {
            if (eventName == SignalREvents.EvictionRemovalStarted)
            {
                // The scan's own thread waits here, inside its remove step, until the test releases it.
                removeStepStarted.TrySetResult();
                release.Task.Wait(TimeSpan.FromSeconds(20));
            }
        };

        var scan = (Task)typeof(CacheReconciliationService)
            .GetMethod("ReconcileCacheFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Scan, [context, scanId, stop.Token, scanNotice])!;
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        await ctx.CompleteDetectionAsync(detection.Id);
        await removeStepStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(await cancellation.ForceKillAsync(scanId).WaitAsync(TimeSpan.FromSeconds(10)));

        // The scan's run ends at once, before its remove step unwinds, and the record keeps the stop for a restart.
        var run = tracker.GetOperation(scanId)!;
        Assert.Equal(1, run.CompletedFlag);
        Assert.Equal(OperationStatus.Cancelled, run.Status);
        Assert.True(ctx.State.LoadOperationRepairs().Single(repair => repair.Id == scanId).RunCancelled);

        stop.Cancel();
        release.TrySetResult();
        await ctx.WaitForRepairAsync(scan, TimeSpan.FromSeconds(10));
        var record = ctx.State.LoadOperationRepairs().Single(repair => repair.Id == scanId);
        Assert.False(record.RunContinues);
        Assert.True(record.RunCancelled);
    }

    [Fact]
    public async Task XDuringTheRemoveStepIsSavedBeforeTheScanEndsAsync()
    {
        using var ctx = new PhaseContext();
        await using var context = ctx.CreateContext();
        context.Downloads.Add(new Download
        {
            Service = PrefillPlatform.Steam.ToService(), ClientIp = "127.0.0.1", Datasource = "Default", GameAppId = 123,
            GameName = "Removed", IsEvicted = true, StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        ctx.State.SetEvictedDataMode(EvictedDataMode.Remove.ToWireString());
        PhaseContext.SetField(ctx.Scan, "_stateService", ctx.State);
        PhaseContext.SetField(ctx.Scan, "_datasourceService", ctx.Datasources);
        PhaseContext.SetField(ctx.Scan, "_cacheScanGate", Idle());
        PhaseContext.SetField(ctx.Scan, "_rustProcessHelper", new ScanResultRustProcessHelper(
            async (operationId, cancellationToken) =>
            {
                var checkpoint = await context.EvictionScanCheckpoints
                    .SingleAsync(item => item.OperationId == operationId, cancellationToken);
                checkpoint.FinalizedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
            }));
        var scanNotice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var scanId = ctx.RegisterScan(scanNotice);
        // The token the scan's worker passes (X on the scan's card cancels it); the removal's own token is linked to it.
        using var stop = new CancellationTokenSource();
        var removeStepStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Notifications.OnSent = (eventName, _) =>
        {
            if (eventName == SignalREvents.EvictionRemovalStarted)
            {
                // The scan's own thread waits here, inside its remove step, until the test releases it.
                removeStepStarted.TrySetResult();
                release.Task.Wait(TimeSpan.FromSeconds(20));
            }
        };

        var scan = (Task)typeof(CacheReconciliationService)
            .GetMethod("ReconcileCacheFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Scan, [context, scanId, stop.Token, scanNotice])!;
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        await ctx.CompleteDetectionAsync(detection.Id);
        await removeStepStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // What the cancel route does for X on the scan's card.
        tracker.CancelOperation(scanId);
        await ctx._operationStateService.RecordCancelAsync(scanId).WaitAsync(TimeSpan.FromSeconds(10));

        // An app stop here restores the gray card: the stop is saved while the run still goes on.
        var saved = ctx.State.LoadOperationRepairs().Single(repair => repair.Id == scanId);
        Assert.True(saved.RunCancelled);
        Assert.True(saved.RunContinues);

        stop.Cancel();
        release.TrySetResult();
        await ctx.WaitForRepairAsync(scan, TimeSpan.FromSeconds(10));
        var outcome = scan.GetType().GetProperty("Result")!.GetValue(scan)!;
        Assert.True((bool)outcome.GetType().GetProperty("Cancelled")!.GetValue(outcome)!);
        Assert.True(tracker.GetOperation(scanId)!.Cancelled);
        var record = ctx.State.LoadOperationRepairs().Single(repair => repair.Id == scanId);
        Assert.False(record.RunContinues);
        Assert.True(record.RunCancelled);
    }

    [Fact]
    public async Task TheScansRecordSaysItsRunGoesOnDuringTheRemoveStepAsync()
    {
        using var ctx = new PhaseContext();
        await using var context = ctx.CreateContext();
        context.Downloads.Add(new Download
        {
            Service = PrefillPlatform.Steam.ToService(), ClientIp = "127.0.0.1", Datasource = "Default", GameAppId = 123,
            GameName = "Removed", IsEvicted = true, StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        ctx.State.SetEvictedDataMode(EvictedDataMode.Remove.ToWireString());
        PhaseContext.SetField(ctx.Scan, "_stateService", ctx.State);
        PhaseContext.SetField(ctx.Scan, "_datasourceService", ctx.Datasources);
        PhaseContext.SetField(ctx.Scan, "_cacheScanGate", Idle());
        PhaseContext.SetField(ctx.Scan, "_rustProcessHelper", new ScanResultRustProcessHelper(
            async (operationId, cancellationToken) =>
            {
                var checkpoint = await context.EvictionScanCheckpoints
                    .SingleAsync(item => item.OperationId == operationId, cancellationToken);
                checkpoint.FinalizedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
            }));
        var scanNotice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var scanId = ctx.RegisterScan(scanNotice);
        using var stop = new CancellationTokenSource();
        var removeStepStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Notifications.OnSent = (eventName, _) =>
        {
            if (eventName == SignalREvents.EvictionRemovalStarted)
            {
                // The scan's own thread waits here, inside its remove step, until the test releases it.
                removeStepStarted.TrySetResult();
                release.Task.Wait(TimeSpan.FromSeconds(20));
            }
        };

        var scan = (Task)typeof(CacheReconciliationService)
            .GetMethod("ReconcileCacheFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Scan, [context, scanId, stop.Token, scanNotice])!;
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        await ctx.CompleteDetectionAsync(detection.Id);
        await removeStepStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A restart here restores the scan red: its results are saved but its run is not over.
        var saved = ctx.State.LoadOperationRepairs().Single(repair => repair.Id == scanId);
        Assert.Equal(OperationStatus.Completed, saved.Outcome);
        Assert.True(saved.RunContinues);

        release.TrySetResult();
        await ctx.WaitForRepairAsync(scan, TimeSpan.FromSeconds(10));
        var record = ctx.State.LoadOperationRepairs().Single(repair => repair.Id == scanId);
        Assert.False(record.RunContinues);
        Assert.False(record.RunCancelled);
        Assert.Null(record.RunError);
    }

    [Fact]
    public async Task AScanThatLeftAFolderUncheckedKeepsItOnItsRepairRecordAsync()
    {
        using var ctx = new PhaseContext();
        await using var context = ctx.CreateContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        PhaseContext.SetField(ctx.Scan, "_stateService", ctx.State);
        PhaseContext.SetField(ctx.Scan, "_datasourceService", ctx.Datasources);
        PhaseContext.SetField(ctx.Scan, "_cacheScanGate", Idle());
        PhaseContext.SetField(ctx.Scan, "_rustProcessHelper", new ScanResultRustProcessHelper(
            async (operationId, cancellationToken) =>
            {
                var checkpoint = await context.EvictionScanCheckpoints
                    .SingleAsync(item => item.OperationId == operationId, cancellationToken);
                checkpoint.FinalizedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
            },
            // What cache_eviction_scan writes when it did not check a cache folder (ScanResult.unchecked_folders).
            """{"success":true,"processed":1,"evicted":0,"unEvicted":0,"uncheckedFolders":["/cache/missing"]}"""));
        var scanNotice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var scanId = ctx.RegisterScan(scanNotice);
        List<string>? saved = null;
        ctx.Notifications.OnSent = (eventName, value) =>
        {
            // Read where a restart would restore from, as the scan moves past the child's result.
            if (eventName == SignalREvents.EvictionScanProgress
                && value is EvictionScanProgress { StageKey: "signalr.evictionScan.postProcessing" })
            {
                saved = ctx._operationStateService.GetPendingRepairs()
                    .Single(repair => repair.Id == scanId).EvictionScan!.UncheckedFolders;
            }
        };

        var scan = (Task)typeof(CacheReconciliationService)
            .GetMethod("ReconcileCacheFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Scan, [context, scanId, CancellationToken.None, scanNotice])!;
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        await ctx.CompleteDetectionAsync(detection.Id);
        await ctx.WaitForRepairAsync(scan, TimeSpan.FromSeconds(10));

        Assert.Equal(["/cache/missing"], saved);
        var warning = Assert.Single(
            Assert.Single(tracker.GetRuns().Runs, row => row.OperationId == scanId).Warnings);
        Assert.Equal("common.notifications.warnings.cacheFoldersUnchecked", warning.StageKey);
        Assert.Equal("/cache/missing", warning.Context["folders"]);
    }

    [Fact]
    public async Task AScanThatCheckedNoCacheFolderFailsNamingThemAsync()
    {
        using var ctx = new PhaseContext();
        await using var context = ctx.CreateContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        PhaseContext.SetField(ctx.Scan, "_stateService", ctx.State);
        PhaseContext.SetField(ctx.Scan, "_datasourceService", ctx.Datasources);
        PhaseContext.SetField(ctx.Scan, "_cacheScanGate", Idle());
        var cachePath = Assert.Single(ctx.Datasources.GetDatasources()).CachePath;
        PhaseContext.SetField(ctx.Scan, "_rustProcessHelper", new ScanResultRustProcessHelper(
            async (operationId, cancellationToken) =>
            {
                var checkpoint = await context.EvictionScanCheckpoints
                    .SingleAsync(item => item.OperationId == operationId, cancellationToken);
                checkpoint.FinalizedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
            },
            // What cache_eviction_scan writes when its only cache root is missing: the root is in uncheckedFolders as
            // the datasource gave it (collect_files_on_disk_with keeps ds.cache_path).
            JsonSerializer.Serialize(new
            {
                success = true,
                processed = 0,
                evicted = 0,
                unEvicted = 0,
                uncheckedFolders = new[] { cachePath }
            })));
        var scanNotice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var scanId = ctx.RegisterScan(scanNotice);

        var scan = (Task)typeof(CacheReconciliationService)
            .GetMethod("ReconcileCacheFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Scan, [context, scanId, CancellationToken.None, scanNotice])!;
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        await ctx.CompleteDetectionAsync(detection.Id);
        await ctx.WaitForRepairAsync(scan, TimeSpan.FromSeconds(10));

        // The scan's outcome is a private record; its Success is what ends the card.
        var outcome = scan.GetType().GetProperty("Result")!.GetValue(scan)!;
        Assert.False((bool)outcome.GetType().GetProperty("Success")!.GetValue(outcome)!);
        var warning = Assert.Single(
            Assert.Single(tracker.GetRuns().Runs, row => row.OperationId == scanId).Warnings);
        Assert.Equal("common.notifications.warnings.cacheFoldersUnchecked", warning.StageKey);
        Assert.Equal(cachePath, warning.Context["folders"]);
    }

    // The Storage page's scan click while a download writes to the cache is handed to the queue like any
    // other click, not refused with a 400: the scan meets the download itself and its schedule holds it on
    // a waiting card until downloads end.
    [Fact]
    public async Task AStorageScanClickDuringADownload_IsQueuedInsteadOfRefused()
    {
        using var ctx = new PhaseContext();
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        Assert.NotNull(CacheScanGateHarness.With(snapshot).CheckDownloadInProgress());
        var enqueued = new List<OperationType>();
        var queue = CacheScanGateHarness.CreateProxy<IOperationQueue>((method, args) =>
        {
            enqueued.Add((OperationType)args![0]!);
            return Task.FromResult(new QueuedOperationResponse
            {
                OperationId = Guid.NewGuid(),
                Queued = false,
                Status = "started"
            });
        });
        var controller = new StatsController(
            context: null!,
            clientGroupsRepository: null!,
            ctx.State,
            Options.Create(new ApiOptions()),
            (ISignalRNotificationService)(object)ctx.Notifications,
            ctx.Scan,
            operationTracker: null!,
            conflictChecker: null!,
            queue,
            new DatasourceCapabilityService(ctx.Datasources),
            clientHostnameService: null!,
            eventsService: null!,
            (RustSpeedTrackerService)RuntimeHelpers.GetUninitializedObject(typeof(RustSpeedTrackerService)),
            operationStateService: null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.ReconcileAsync(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal([OperationType.EvictionScan], enqueued);
    }

    [Fact]
    public async Task ACanceledEvictionScanStaysCanceledAsync()
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        // A cancel from the card kills the running scan, and the scan sees the cancel as the process helper reports it.
        PhaseContext.SetField(ctx.Scan, "_rustProcessHelper", new ScanResultRustProcessHelper((operationId, _) =>
        {
            tracker.CancelOperation(operationId);
            throw new OperationCanceledException();
        }));
        var scanId = Assert.IsType<Guid>(typeof(CacheReconciliationService)
            .GetMethod("StartScanInBackground", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Scan, ["Eviction Scan", new RunNotice(NotificationMode.All, RunTrigger.Manual), null]));
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        await ctx.CompleteDetectionAsync(detection.Id);

        Assert.Equal(OperationStatus.Cancelled, (await WaitForTerminalAsync(tracker, scanId)).Status);
    }

    // The scan's own refusal is what marks its row as turned away for a download; the schedule holds the run
    // on that mark, so without it a download that ends before the schedule looks loses the run.
    [Fact]
    public async Task AnEvictionScanTurnedAwayForADownload_MarksItsRowAsync()
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        PhaseContext.SetField(ctx.Scan, "_cacheScanGate", CacheScanGateHarness.With(snapshot));

        var scanId = Assert.IsType<Guid>(typeof(CacheReconciliationService)
            .GetMethod("StartScanInBackground", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Scan, ["Eviction Scan", new RunNotice(NotificationMode.All, RunTrigger.Manual), null]));

        var ended = await WaitForTerminalAsync(tracker, scanId);
        Assert.Equal(OperationStatus.Skipped, ended.Status);
        Assert.True(ended.SkippedForDownload);
    }

    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.Manual, RunTrigger.RunAll, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Manual, RunVisibility.Background)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Scheduled, RunVisibility.Hidden)]
    public void TheScanRegistersWithTheNoticeItWasAdmittedWith(NotificationMode mode, RunTrigger trigger, RunVisibility expected)
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var notice = new RunNotice(mode, trigger);

        var id = ctx.RegisterScan(notice);

        Assert.Same(notice, tracker.GetOperation(id)!.Notice);
        Assert.Equal(expected, Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == id).Visibility);
    }

    [Theory]
    [InlineData(RunTrigger.Manual)]
    [InlineData(RunTrigger.Scheduled)]
    public async Task WithNoDownloadsARunNowEndsSkippedWithItsNoticeAndAnAutomaticRunLeavesNoRow(RunTrigger trigger)
    {
        using var ctx = new PhaseContext();
        await using var database = await TestDatabase.CreateAsync();
        await using var context = new AppDbContext(database.Options);
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var notice = new RunNotice(NotificationMode.All, trigger);
        ctx.Scan.SelectRunNotice(notice);
        using var services = new ServiceCollection().AddSingleton(context).BuildServiceProvider();

        await (Task)typeof(CacheReconciliationService).GetMethod("ExecuteWorkAsync",
            BindingFlags.Instance | BindingFlags.NonPublic, [typeof(IServiceProvider), typeof(CancellationToken)])!
            .Invoke(ctx.Scan, [services, CancellationToken.None])!;

        var runs = tracker.GetRuns().Runs;
        if (trigger != RunTrigger.Manual)
        {
            Assert.Empty(runs);
            return;
        }

        var row = Assert.Single(runs);
        Assert.Equal(OperationStatus.Skipped.ToWireString(), row.Status);
        Assert.Equal(RunVisibility.Card, row.Visibility);
        Assert.Same(notice, tracker.GetOperation(row.OperationId)!.Notice);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACompletedClearEvictsTheFinishedDownloadsNoScanWillReach(bool skipsCacheScan)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var harness = new DatabaseReconciliation(database, database.Options);
        var clearStartedAt = DateTime.UtcNow.AddMinutes(-10);
        var receipt = Path.Combine(harness.Root, ".lancache-repair-receipt.json");
        await File.WriteAllTextAsync(receipt, "{}");
        await using (var seed = new AppDbContext(database.Options))
        {
            // 'default' has no key scheme, so no scan can reach it; 'secondary' has a receipt and
            // a key scheme, so only a skipped scan leaves it to this evict.
            seed.Downloads.AddRange(
                ClearedDownload("unscanned-before", "Default", clearStartedAt.AddMinutes(-1), gameAppId: 10),
                ClearedDownload("unscanned-after", "Default", clearStartedAt.AddMinutes(1), gameAppId: 11),
                ClearedDownload("unscanned-active", "Default", clearStartedAt.AddMinutes(-1), gameAppId: 12, isActive: true),
                ClearedDownload("unscanned-zero-byte", "Default", clearStartedAt.AddMinutes(-1), gameAppId: 13, bytes: 0),
                ClearedDownload("unscanned-service", "Default", clearStartedAt.AddMinutes(-1), service: "wsus"),
                ClearedDownload("receipt-before", "secondary", clearStartedAt.AddMinutes(-1), gameAppId: 20),
                ClearedDownload("not-cleared", "third", clearStartedAt.AddMinutes(-1), gameAppId: 30));
            seed.CachedGameDetections.AddRange(
                CachedGame(10, totalSizeBytes: 1024),
                CachedGame(20, totalSizeBytes: 2048),
                CachedGame(30, totalSizeBytes: 512));
            seed.CachedServiceDetections.Add(new CachedServiceDetection
            {
                ServiceName = "wsus",
                CacheFilesFound = 3,
                TotalSizeBytes = 4096
            });
            AddPrefillRows(seed);
            await seed.SaveChangesAsync();
        }
        await new GameCacheDetectionDataService(
                database.Factory,
                NullLogger<GameCacheDetectionDataService>.Instance)
            .RefreshDiskSummaryAsync();
        await using (var before = new AppDbContext(database.Options))
        {
            Assert.Equal(7680UL, (await before.CachedDetectionSummaries.SingleAsync()).IdentifiedCacheBytes);
        }

        // The clear kept no file of 'default', so its downloads go; 'secondary' is decided by the scan rules.
        var defaultSource = ClearSource("default", keyScheme: null, receiptPath: null);
        defaultSource.NativeCompletionAccepted = true;

        await harness.Scan.EvictClearedSourcesAsync(
            ClearRepair(
                clearStartedAt,
                OperationStatus.Completed,
                defaultSource,
                ClearSource("secondary", "monolithic", receipt)),
            skipsCacheScan,
            CancellationToken.None);

        await using var verify = new AppDbContext(database.Options);
        var evicted = await verify.Downloads.ToDictionaryAsync(row => row.ClientIp, row => row.IsEvicted);
        Assert.True(evicted["unscanned-before"]);
        Assert.False(evicted["unscanned-after"]);
        Assert.False(evicted["unscanned-active"]);
        Assert.False(evicted["unscanned-zero-byte"]);
        Assert.True(evicted["unscanned-service"]);
        Assert.Equal(skipsCacheScan, evicted["receipt-before"]);
        Assert.False(evicted["not-cleared"]);
        var games = await verify.CachedGameDetections.ToDictionaryAsync(row => row.GameAppId, row => row.IsEvicted);
        Assert.True(games[10]);
        Assert.Equal(skipsCacheScan, games[20]);
        Assert.False(games[30]);
        var service = await verify.CachedServiceDetections.SingleAsync();
        Assert.True(service.IsEvicted);
        Assert.Equal(0, service.CacheFilesFound);
        Assert.Equal(0UL, service.TotalSizeBytes);
        var summary = await verify.CachedDetectionSummaries.SingleAsync();
        Assert.Equal(skipsCacheScan ? 512UL : 2560UL, summary.IdentifiedCacheBytes);
        Assert.Empty(await verify.PrefillCachedDepots.ToListAsync());
        Assert.Empty(await verify.PrefillCachedApps.ToListAsync());
        Assert.Equal(1, harness.Notifications.Count(SignalREvents.PrefillCacheChanged));
    }

    [Theory]
    [InlineData(OperationStatus.Cancelled)]
    [InlineData(OperationStatus.Failed)]
    public async Task AClearThatDidNotCompleteEvictsNothingAndKeepsThePrefillBadges(OperationStatus outcome)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var harness = new DatabaseReconciliation(database, database.Options);
        var clearStartedAt = DateTime.UtcNow.AddMinutes(-10);
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.Downloads.Add(ClearedDownload("before", "default", clearStartedAt.AddMinutes(-1), gameAppId: 10));
            AddPrefillRows(seed);
            await seed.SaveChangesAsync();
        }

        await harness.Scan.EvictClearedSourcesAsync(
            ClearRepair(clearStartedAt, outcome, ClearSource("default", keyScheme: null, receiptPath: null)),
            skipsCacheScan: true,
            CancellationToken.None);

        await using var verify = new AppDbContext(database.Options);
        Assert.False((await verify.Downloads.SingleAsync()).IsEvicted);
        Assert.Single(await verify.PrefillCachedDepots.ToListAsync());
        Assert.Single(await verify.PrefillCachedApps.ToListAsync());
        Assert.Equal(0, harness.Notifications.Count(SignalREvents.PrefillCacheChanged));
    }

    [Fact]
    public async Task AClearThatFailedElsewhereEvictsTheRowsOfASourceNoScanCanCheckAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var harness = new DatabaseReconciliation(database, database.Options);
        var clearStartedAt = DateTime.UtcNow.AddMinutes(-10);
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.Downloads.AddRange(
                ClearedDownload("failed-source", "failed", clearStartedAt.AddMinutes(-1), gameAppId: 10),
                ClearedDownload("cleared-unscannable", "mixed", clearStartedAt.AddMinutes(-1), gameAppId: 20));
            await seed.SaveChangesAsync();
        }
        // The first datasource's clear failed; the second has no key scheme and its clear finished.
        var cleared = ClearSource("mixed", keyScheme: null, receiptPath: null);
        cleared.NativeCompletionAccepted = true;

        await harness.Scan.EvictClearedSourcesAsync(
            ClearRepair(clearStartedAt, OperationStatus.Failed, ClearSource("failed", "monolithic", receiptPath: null), cleared),
            skipsCacheScan: false,
            CancellationToken.None);

        await using var verify = new AppDbContext(database.Options);
        var evicted = await verify.Downloads.ToDictionaryAsync(row => row.ClientIp, row => row.IsEvicted);
        Assert.False(evicted["failed-source"]);
        Assert.True(evicted["cleared-unscannable"]);
    }

    [Fact]
    public async Task ACompletedClearThatKeptFilesOnASourceWithNoKeySchemeLeavesItsDownloadsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var harness = new DatabaseReconciliation(database, database.Options);
        var clearStartedAt = DateTime.UtcNow.AddMinutes(-10);
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.Downloads.Add(ClearedDownload("kept-files", "default", clearStartedAt.AddMinutes(-1), gameAppId: 10));
            await seed.SaveChangesAsync();
        }
        // The clear kept files of this source, and no scan can check a source without a key scheme.
        var kept = ClearSource("default", keyScheme: null, receiptPath: null);
        kept.NativeCompletionAccepted = false;

        await harness.Scan.EvictClearedSourcesAsync(
            ClearRepair(clearStartedAt, OperationStatus.Completed, kept),
            skipsCacheScan: false,
            CancellationToken.None);

        await using var verify = new AppDbContext(database.Options);
        Assert.False((await verify.Downloads.SingleAsync()).IsEvicted);
    }

    [Fact]
    public async Task AFailureInsideTheClearEvictRollsBackTheEvictionAndThePrefillWipe()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clearStartedAt = DateTime.UtcNow.AddMinutes(-10);
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.Downloads.Add(ClearedDownload("before", "default", clearStartedAt.AddMinutes(-1), gameAppId: 10));
            AddPrefillRows(seed);
            await seed.SaveChangesAsync();
            await seed.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION "PreventPrefillAppDelete"() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'blocked prefill app deletion';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "PreventPrefillAppDelete"
                BEFORE DELETE ON "PrefillCachedApps"
                FOR EACH ROW
                EXECUTE FUNCTION "PreventPrefillAppDelete"();
                """);
        }

        // A retrying strategy refuses a transaction opened outside it, so this also proves the
        // evict and the wipe run inside the execution strategy.
        string connectionString;
        await using (var context = database.Factory.CreateDbContext())
        {
            connectionString = context.Database.GetConnectionString()!;
        }
        var retrying = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, options => options.EnableRetryOnFailure(3, TimeSpan.Zero, null))
            .Options;
        using var harness = new DatabaseReconciliation(database, retrying);

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => harness.Scan.EvictClearedSourcesAsync(
            ClearRepair(
                clearStartedAt,
                OperationStatus.Completed,
                ClearSource("default", keyScheme: null, receiptPath: null)),
            skipsCacheScan: true,
            CancellationToken.None));

        await using var verify = new AppDbContext(database.Options);
        Assert.False((await verify.Downloads.SingleAsync()).IsEvicted);
        Assert.Single(await verify.PrefillCachedDepots.ToListAsync());
        Assert.Single(await verify.PrefillCachedApps.ToListAsync());
        Assert.Equal(0, harness.Notifications.Count(SignalREvents.PrefillCacheChanged));
    }

    [Fact]
    public async Task AServiceIsEvictedOnlyWhenEveryByteBackedServiceDownloadIsEvicted()
    {
        await using var database = await TestDatabase.CreateAsync();
        var ended = DateTime.UtcNow.AddMinutes(-1);
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.Downloads.AddRange(
                ClearedDownload("all-evicted", "default", ended, service: "WSUS", isEvicted: true),
                ClearedDownload("one-cached-evicted", "default", ended, service: "xboxlive", isEvicted: true),
                ClearedDownload("one-cached-live", "default", ended, service: "xboxlive"),
                ClearedDownload("zero-byte-evicted", "default", ended, service: "riot", bytes: 0, isEvicted: true),
                ClearedDownload("named-evicted", "default", ended, service: "blizzard", isEvicted: true),
                ClearedDownload("named-live", "default", ended, service: "blizzard", gameName: "Diablo"));
            seed.CachedServiceDetections.AddRange(
                CachedService("wsus"),
                CachedService("xboxlive"),
                CachedService("riot"),
                CachedService("blizzard"));
            await seed.SaveChangesAsync();
        }

        await using (var context = new AppDbContext(database.Options))
        {
            Assert.Equal(1, await CacheReconciliationService.EvictCachedServiceDetectionsAsync(
                context,
                NullLogger.Instance,
                CancellationToken.None));

            // The self-heal reads the same Download set, so it must not undo the evict.
            Assert.Equal(0, await CacheReconciliationService.UnevictCachedServiceDetectionsAsync(
                context,
                NullLogger.Instance,
                new GameCacheDetectionDataService(
                    database.Factory,
                    NullLogger<GameCacheDetectionDataService>.Instance),
                CancellationToken.None));
        }

        await using var verify = new AppDbContext(database.Options);
        var services = await verify.CachedServiceDetections.ToDictionaryAsync(row => row.ServiceName);
        Assert.True(services["wsus"].IsEvicted);
        Assert.Equal(0, services["wsus"].CacheFilesFound);
        Assert.Equal(0UL, services["wsus"].TotalSizeBytes);
        foreach (var kept in new[] { "xboxlive", "riot", "blizzard" })
        {
            Assert.False(services[kept].IsEvicted);
            Assert.Equal(3, services[kept].CacheFilesFound);
            Assert.Equal(4096UL, services[kept].TotalSizeBytes);
        }
    }

    [Fact]
    public async Task AFinalizedScanThatEvictedDownloadsEvictsTheirService()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var harness = new DatabaseReconciliation(database, database.Options);
        var scanId = Guid.NewGuid();
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.Downloads.Add(ClearedDownload(
                "evicted",
                "default",
                DateTime.UtcNow.AddMinutes(-1),
                service: "wsus",
                isEvicted: true));
            seed.CachedServiceDetections.Add(CachedService("wsus"));
            seed.EvictionScanCheckpoints.Add(new EvictionScanCheckpoint
            {
                OperationId = scanId,
                Processed = 1,
                Evicted = 1,
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-2)
            });
            await seed.SaveChangesAsync();
        }

        await (Task)typeof(CacheReconciliationService)
            .GetMethod("FinalizeEvictionScanAttemptAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(harness.Scan,
            [
                new OperationRepair
                {
                    Id = Guid.NewGuid(),
                    Type = OperationType.EvictionScan,
                    Name = "Eviction Scan",
                    StartedAt = DateTime.UtcNow.AddMinutes(-3)
                },
                scanId,
                false,
                CancellationToken.None,
                true
            ])!;

        await using var verify = new AppDbContext(database.Options);
        var service = await verify.CachedServiceDetections.SingleAsync();
        Assert.True(service.IsEvicted);
        Assert.Equal(0, service.CacheFilesFound);
        Assert.Equal(0UL, service.TotalSizeBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("steam")]
    public async Task ThePurgeRunnerKeepsTheEvictionPurgeArgumentsAndInputAndReopensBeforeReadingTheReport(
        string? service)
    {
        using var ctx = new PhaseContext();
        var source = Assert.Single(ctx.Datasources.GetDatasources());
        await File.WriteAllLinesAsync(Path.Combine(source.LogPath, "access.log"), ["GET /a HTTP/1.1"]);
        ctx.State.SetLogSourcePositions(source.Name, new Dictionary<string, long> { ["access"] = 10 });
        ctx.State.SetLogTotalLines(source.Name, 20);
        var paths = new TempDirPathResolver(ctx.Root) { DockerSocketAvailable = true };
        var rust = new CapturingPurgeRust(paths);
        var reopenedWithReportUnread = false;
        var positionAtReopen = 0L;
        var nginx = new SaveNginxLogRotationService(source.LogPath, paths, () =>
        {
            reopenedWithReportUnread = File.Exists(rust.OutputPath);
            positionAtReopen = ctx.State.GetLogSourcePositions(source.Name)["access"];
        });
        // Urls and depots as the eviction collectors build them; a service purge takes no depots.
        IReadOnlyList<string> urls = service is null ? ["/a", "/b"] : ["/a"];
        IReadOnlyList<long> depotIds = service is null ? [7, 9] : [];

        var repair = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.EvictionScan,
            Name = "Eviction Scan",
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            Sources = [new OperationRepairSource { Datasource = source.Name }],
            EvictionScan = new EvictionScanRepair()
        };
        await ctx._operationStateService.PrepareRepairAsync(repair, CancellationToken.None);
        await ctx._operationStateService.StartWorkAsync(repair.Id, datasource: null, CancellationToken.None);

        var report = await new LogPurgeRunner(
                paths,
                rust,
                nginx,
                ctx.State,
                ctx._operationStateService,
                NullLogger.Instance)
            .RunAsync(repair.Id, source, new LogPurgeTargets(urls, depotIds, service), null, CancellationToken.None);

        // The input the eviction purge wrote before the runner existed, byte for byte.
        var evictionInput = JsonSerializer.Serialize(
            new { urls, depot_ids = depotIds },
            new JsonSerializerOptions { WriteIndented = false });
        var expectedInput = service is null
            ? evictionInput
            : evictionInput[..^1] + $",\"service\":\"{service}\"}}";
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(expectedInput), rust.Input);
        var arguments = Regex.Match(
            rust.Arguments,
            "^\"(?<logs>[^\"]+)\" \"[^\"]+\" \"[^\"]+\" --progress-json \"[^\"]+\" --progress --stem-positions \"[^\"]+\"$");
        Assert.True(arguments.Success, rust.Arguments);
        Assert.Equal(source.LogPath, arguments.Groups["logs"].Value);
        Assert.True(reopenedWithReportUnread);
        Assert.Equal(10, positionAtReopen);
        Assert.False(File.Exists(rust.OutputPath));
        Assert.Equal(2, report.LinesRemoved);
        Assert.Equal(9, ctx.State.GetLogSourcePositions(source.Name)["access"]);
        // The step was marked started once its reopen check passed.
        Assert.True(Assert.Single(ctx._operationStateService.GetPendingRepairs()
            .Single(pending => pending.Id == repair.Id).Sources).LogRewriteStarted);
    }

    [Fact]
    public async Task TheEvictionLogStepStartsItsWorkFirstAndHoldsOneLockFromItsTargetsToItsRowDeletes()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", ["access.log"], false));
        await SeedEvictedDownloadAsync(run.Database, "alpha", "/remove", depotId: 7);
        run.State.SetLogSourcePositions("alpha", new Dictionary<string, long> { ["access"] = 10 });
        run.State.SetLogTotalLines("alpha", 20);
        var importId = run.Tracker.RegisterOperation(
            OperationType.LogProcessing,
            "Log import",
            new CancellationTokenSource());
        var import = await run.Repairs.LockLogFilesAsync(
            importId,
            OperationType.LogProcessing,
            LogFileLockKind.Ingest,
            CancellationToken.None);
        var lockedWhileDeleting = false;
        run.Notifications.OnSent = (_, value) =>
        {
            if (value is EvictionRemovalProgress { StageKey: "signalr.evictionRemove.removingDownloads" })
            {
                lockedWhileDeleting = run.Repairs.WaitForLogStepAsync(active: true, CancellationToken.None)
                    .Wait(TimeSpan.FromSeconds(5));
            }
        };

        var removalId = await run.Scan.StartBulkEvictionRemovalAsync(CancellationToken.None);

        // The step names the import it waits behind, so every start it makes has returned by then.
        await WaitUntilAsync(() => run.Tracker.GetOperation(removalId)?.BlockedByName == "Log import");
        var waiting = run.ReadRepair(removalId);
        Assert.True(waiting.DatabaseWriteStarted);
        var waitingSource = Assert.Single(waiting.Sources);
        Assert.True(waitingSource.NativeLaunchAuthorized);
        Assert.False(waitingSource.LogRewriteStarted);
        Assert.Empty(run.Rust.Runs);

        // Cached again while the step waits: the targets are read under the lock, so its depot stays out.
        await using (var refill = new AppDbContext(run.Database.Options))
        {
            var cachedAgain = ClearedDownload("cached-again", "alpha", DateTime.UtcNow.AddHours(-1));
            cachedAgain.DepotId = 7;
            refill.Downloads.Add(cachedAgain);
            await refill.SaveChangesAsync();
        }
        await import.DisposeAsync();

        Assert.Equal(OperationStatus.Completed, (await WaitForTerminalAsync(run.Tracker, removalId)).Status);
        var repair = await run.WaitForCompletedRepairAsync(removalId);
        var source = Assert.Single(repair.Sources);
        Assert.True(source.LogRewriteStarted);
        Assert.True(source.LogPositionsKept);
        using var input = JsonDocument.Parse(Assert.Single(run.Rust.Runs).Input);
        Assert.Empty(input.RootElement.GetProperty("depot_ids").EnumerateArray());
        Assert.Equal("/remove", Assert.Single(input.RootElement.GetProperty("urls").EnumerateArray()).GetString());
        Assert.True(lockedWhileDeleting);
        // Kept, so no reset: the position came back only by the line the purge removed below it.
        Assert.Equal(9, run.State.GetLogSourcePositions("alpha")["access"]);
        await run.Repairs.WaitForLogStepAsync(active: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await using var verify = new AppDbContext(run.Database.Options);
        Assert.Equal("cached-again", (await verify.Downloads.SingleAsync()).ClientIp);
        Assert.False(await verify.LogEntries.AnyAsync());
    }

    [Fact]
    public async Task AnEvictionRemovalCanceledBeforeItsLogStepStoresNoStartedFlagAndKeepsThePositions()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", ["access.log"], false));
        await SeedEvictedDownloadAsync(run.Database, "alpha", "/remove");
        run.State.SetLogSourcePositions("alpha", new Dictionary<string, long> { ["access"] = 10 });
        var importId = run.Tracker.RegisterOperation(
            OperationType.LogProcessing,
            "Log import",
            new CancellationTokenSource());
        await using var import = await run.Repairs.LockLogFilesAsync(
            importId,
            OperationType.LogProcessing,
            LogFileLockKind.Ingest,
            CancellationToken.None);
        var removalId = await run.Scan.StartBulkEvictionRemovalAsync(CancellationToken.None);
        await WaitUntilAsync(() => run.Tracker.GetOperation(removalId)?.BlockedByName == "Log import");

        run.Tracker.CancelOperation(removalId);

        Assert.Equal(OperationStatus.Cancelled, (await WaitForTerminalAsync(run.Tracker, removalId)).Status);
        // Nothing to reset, so the repair ends while the import still holds the logs.
        var repair = await run.WaitForCompletedRepairAsync(removalId);
        Assert.Equal(OperationStatus.Cancelled, repair.Outcome);
        Assert.False(Assert.Single(repair.Sources).LogRewriteStarted);
        Assert.Empty(run.Rust.Runs);
        Assert.Equal(10, run.State.GetLogSourcePositions("alpha")["access"]);
        await using var verify = new AppDbContext(run.Database.Options);
        Assert.True(await verify.Downloads.AnyAsync(download => download.IsEvicted));
    }

    [Fact]
    public async Task AnEvictionLogStepRefusedBeforeItTouchesALogResetsNoPosition()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", ["access.log"], false));
        await SeedEvictedDownloadAsync(run.Database, "alpha", "/remove");
        run.State.SetLogSourcePositions("alpha", new Dictionary<string, long> { ["access"] = 10 });
        var logs = run.Datasources.GetDatasource("alpha")!.LogPath;
        // logrotate renames access.log while the step looks for its writer, so the check cannot read the
        // file it bound and refuses the step before any child starts.
        run.Nginx.DuringWriterSearch = () =>
        {
            run.Nginx.DuringWriterSearch = null;
            File.Move(Path.Combine(logs, "access.log"), Path.Combine(logs, "access.log.1"));
        };

        var removalId = await run.Scan.StartBulkEvictionRemovalAsync(CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, (await WaitForTerminalAsync(run.Tracker, removalId)).Status);
        var repair = await run.WaitForCompletedRepairAsync(removalId);
        Assert.Empty(run.Rust.Runs);
        Assert.False(Assert.Single(repair.Sources).LogRewriteStarted);
        Assert.Equal(10, run.State.GetLogSourcePositions("alpha")["access"]);
        await using var verify = new AppDbContext(run.Database.Options);
        Assert.True(await verify.Downloads.AnyAsync(download => download.IsEvicted));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task AnEvictionLogStepStoppedAfterAPurgeIsFinishedWholeByItsRepair(int datasourceCount, bool forceStop)
    {
        var names = new[] { "alpha", "beta" }[..datasourceCount];
        await using var run = await RepairRun.CreateAsync(
            names.Select(name => (name, new[] { "access.log" }, false)).ToArray());
        foreach (var name in names)
        {
            await SeedEvictedDownloadAsync(run.Database, name, $"/{name}-evicted");
            run.State.SetLogSourcePositions(name, new Dictionary<string, long> { ["access"] = 10 });
            run.State.SetLogTotalLines(name, 20);
        }
        bool? secondStartedWhenRedone = null;
        var evictedRowsAtLastPurge = -1;
        run.Rust.OnRun = async (index, operationId) =>
        {
            if (index == 1)
            {
                // The stop arrives once the first datasource's child has rewritten its log.
                if (forceStop)
                {
                    await run.Repairs.RecordForceStopAsync(operationId);
                }
                run.Tracker.CancelOperation(operationId);
            }
            else if (index == 2 && datasourceCount == 2)
            {
                secondStartedWhenRedone = run.ReadRepair(operationId).Sources
                    .Single(source => source.Datasource == "beta").LogRewriteStarted;
            }

            if (index == datasourceCount + 1)
            {
                await using var context = new AppDbContext(run.Database.Options);
                evictedRowsAtLastPurge = await context.Downloads.CountAsync(download => download.IsEvicted)
                    + await context.LogEntries.CountAsync();
            }
        };

        var removalId = await run.Scan.StartBulkEvictionRemovalAsync(CancellationToken.None);

        Assert.Equal(OperationStatus.Cancelled, (await WaitForTerminalAsync(run.Tracker, removalId)).Status);
        var repair = await run.WaitForCompletedRepairAsync(removalId);
        Assert.All(repair.Sources, source =>
        {
            Assert.True(source.NativeLaunchAuthorized);
            Assert.True(source.LogRewriteStarted);
            Assert.True(source.LogPositionsKept);
        });
        // The stopped run purged the first datasource; the repair purged every datasource again.
        Assert.Equal(names.Prepend(names[0]), run.Rust.Runs.Select(purge => Path.GetFileName(purge.Logs)));
        Assert.Contains($"/{names[^1]}-evicted", run.Rust.Runs[^1].Input, StringComparison.Ordinal);
        // No evicted row went before every datasource's log was purged.
        Assert.Equal(datasourceCount * 2, evictedRowsAtLastPurge);
        await using (var verify = new AppDbContext(run.Database.Options))
        {
            Assert.False(await verify.Downloads.AnyAsync());
            Assert.False(await verify.LogEntries.AnyAsync());
        }
        // The first log was rewritten when the step stopped, so its datasource imports from the
        // start; a datasource first purged by the repair keeps its position, moved by its own purge.
        Assert.Empty(run.State.GetLogSourcePositions("alpha"));
        Assert.Equal(0, run.State.GetLogTotalLines("alpha"));
        if (datasourceCount == 2)
        {
            Assert.False(secondStartedWhenRedone);
            Assert.Equal(9, run.State.GetLogSourcePositions("beta")["access"]);
        }
    }

    [Fact]
    public async Task ACanceledEvictionRemovalStillNamesAMissingLogFolderItsRepairRemovedRowsFromAsync()
    {
        await using var run = await RepairRun.CreateAsync(
            ("alpha", new[] { "access.log" }, false),
            ("beta", new[] { "access.log" }, false));
        foreach (var name in new[] { "alpha", "beta" })
        {
            await SeedEvictedDownloadAsync(run.Database, name, $"/{name}-evicted");
        }
        var beta = run.Datasources.GetDatasource("beta")!;
        // The key recipe is configured, so the missing folder does not turn the evidence unknown first.
        beta.SchemeOverride = DatasourceSchemeOverride.Monolithic;
        Directory.Delete(beta.LogPath, recursive: true);
        run.Rust.OnRun = (index, operationId) =>
        {
            if (index == 1)
            {
                run.Tracker.CancelOperation(operationId);
            }
            return Task.CompletedTask;
        };

        var removalId = await run.Scan.StartBulkEvictionRemovalAsync(CancellationToken.None);

        Assert.Equal(OperationStatus.Cancelled, (await WaitForTerminalAsync(run.Tracker, removalId)).Status);
        await run.WaitForCompletedRepairAsync(removalId);
        var row = Assert.Single(run.Tracker.GetRuns().Runs, item => item.OperationId == removalId);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("common.notifications.warnings.logFoldersMissing", warning.StageKey);
        Assert.Equal("beta", warning.Context["datasources"]);
        await using var verify = new AppDbContext(run.Database.Options);
        Assert.False(await verify.Downloads.AnyAsync(download => download.IsEvicted));
    }

    [Fact]
    public async Task AnEvictionRemovalWhosePurgeHitAPermissionErrorNamesTheDatasourceAsync()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", new[] { "access.log" }, false));
        await SeedEvictedDownloadAsync(run.Database, "alpha", "/remove");
        run.Rust.PermissionErrors = 1;

        var removalId = await run.Scan.StartBulkEvictionRemovalAsync(CancellationToken.None);

        Assert.Equal(OperationStatus.Completed, (await WaitForTerminalAsync(run.Tracker, removalId)).Status);
        var row = Assert.Single(run.Tracker.GetRuns().Runs, item => item.OperationId == removalId);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("common.notifications.warnings.logLinesKept", warning.StageKey);
        Assert.Equal("alpha", warning.Context["datasources"]);
    }

    [Fact]
    public async Task AnEvictionRemovalWithoutItsPurgeProgramNamesTheDatasourcesAsync()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", new[] { "access.log" }, false));
        await SeedEvictedDownloadAsync(run.Database, "alpha", "/remove");
        File.Delete(new TempDirPathResolver(run.Root).GetRustLogPurgePath());

        var removalId = await run.Scan.StartBulkEvictionRemovalAsync(CancellationToken.None);

        Assert.Equal(OperationStatus.Completed, (await WaitForTerminalAsync(run.Tracker, removalId)).Status);
        Assert.Empty(run.Rust.Runs);
        var row = Assert.Single(run.Tracker.GetRuns().Runs, item => item.OperationId == removalId);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("common.notifications.warnings.logPurgeProgramMissing", warning.StageKey);
        Assert.Equal("alpha", warning.Context["datasources"]);
    }

    [Fact]
    public async Task AnEvictionRemovalStartedBehindARepairThatOwesAResetLetsBothFinish()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", ["access.log"], false));
        await SeedEvictedDownloadAsync(run.Database, "alpha", "/remove");
        var alpha = Assert.Single(run.Datasources.GetDatasources());
        // An earlier eviction removal stopped inside its log step, so its repair resets the
        // positions under the lock before it finishes that step.
        var earlier = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.EvictionRemoval,
            Name = "Eviction Removal",
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            Sources =
            [
                new OperationRepairSource
                {
                    Datasource = alpha.Name,
                    LogRoot = alpha.LogPath,
                    CacheRoot = alpha.CachePath,
                    KeyScheme = LogSourceLayout.LayoutMonolithic,
                    ResetLogPositions = true,
                    RefreshDownloads = true,
                    RefreshDetection = true,
                    InvalidateCorruption = true
                }
            ],
            EvictionRemoval = new EvictionRemovalRepair
            {
                Selection = new EvictionRemovalMetadata(),
                StageKey = "signalr.evictionRemove.starting.bulk"
            }
        };
        await run.Repairs.PrepareRepairAsync(earlier, CancellationToken.None);
        await run.Repairs.StartWorkAsync(earlier.Id, alpha.Name, CancellationToken.None);
        await run.Repairs.StartWorkAsync(earlier.Id, datasource: null, CancellationToken.None);
        await run.Repairs.MarkLogRewriteStartedAsync(earlier.Id, alpha.Name);
        var import = await run.Repairs.LockLogFilesAsync(
            operationId: null,
            OperationType.LogProcessing,
            LogFileLockKind.Ingest,
            CancellationToken.None);
        await run.Repairs.FinishRepairAsync(earlier.Id, success: false, cancelled: true, error: null);

        var removalId = await run.Scan.StartBulkEvictionRemovalAsync(CancellationToken.None);
        await WaitUntilAsync(() => run.State.LoadOperationRepairs().Any(repair => repair.Id == removalId));
        var removal = WaitForTerminalAsync(run.Tracker, removalId);

        // Its starts wait for the earlier repair, which waits for the import.
        Assert.NotSame(removal, await Task.WhenAny(removal, Task.Delay(300)));
        Assert.Equal(OperationRepairPhase.Prepared, run.ReadRepair(removalId).Phase);
        await import.DisposeAsync();

        Assert.Equal(OperationStatus.Completed, (await removal).Status);
        Assert.Equal(OperationStatus.Cancelled, (await run.WaitForCompletedRepairAsync(earlier.Id)).Outcome);
        await run.WaitForCompletedRepairAsync(removalId);
        await using var verify = new AppDbContext(run.Database.Options);
        Assert.False(await verify.Downloads.AnyAsync());
    }

    [Fact]
    public async Task RemovingOrphanedDownloadsWaitsWhileAnotherStepHoldsTheLogs()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", ["access.log"], false));
        long orphanId;
        await using (var seed = new AppDbContext(run.Database.Options))
        {
            var orphan = ClearedDownload("orphan", "alpha", DateTime.UtcNow.AddHours(-1));
            seed.Downloads.Add(orphan);
            await seed.SaveChangesAsync();
            orphanId = orphan.Id;
        }
        var step = await run.Repairs.LockLogFilesAsync(
            operationId: null,
            OperationType.EvictionRemoval,
            LogFileLockKind.Rewrite,
            CancellationToken.None);
        await using var context = new AppDbContext(run.Database.Options);

        var removal = run.Scan.RemoveOrphanedDownloadsAsync(context, [orphanId], CancellationToken.None);

        Assert.NotSame(removal, await Task.WhenAny(removal, Task.Delay(300)));
        await using (var during = new AppDbContext(run.Database.Options))
        {
            Assert.True(await during.Downloads.AnyAsync(download => download.Id == orphanId));
        }
        await step.DisposeAsync();
        Assert.Equal(1, await removal.WaitAsync(TimeSpan.FromSeconds(5)));
        await run.Repairs.WaitForLogStepAsync(active: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("both", true)]
    [InlineData("monolithic", false)]
    public async Task AClearWithoutAKeyRecipeOrOfAnEmptyRootEvictsTheDownloadsThatEndedBeforeIt(
        string layout,
        bool cacheFiles)
    {
        string[] logFiles = layout switch
        {
            "none" => [],
            "both" => ["access.log", "steam-access.log"],
            _ => ["access.log"]
        };
        await using var run = await RepairRun.CreateAsync(("alpha", logFiles, cacheFiles));
        await using (var seed = new AppDbContext(run.Database.Options))
        {
            // Downloads.Datasource drifts in case from the configured name.
            seed.Downloads.AddRange(
                ClearedDownload("before", "Alpha", DateTime.UtcNow.AddHours(-1), gameAppId: 10),
                ClearedDownload("after", "Alpha", DateTime.UtcNow.AddHours(1), gameAppId: 11));
            await seed.SaveChangesAsync();
        }

        var clearId = Assert.IsType<Guid>(await run.Clearing.StartCacheClearAsync());

        var terminal = await WaitForTerminalAsync(run.Tracker, clearId);
        Assert.True(terminal.Status == OperationStatus.Completed, terminal.Message);
        Assert.Equal(OperationStatus.Completed, (await run.WaitForCompletedRepairAsync(clearId)).Outcome);
        await using var verify = new AppDbContext(run.Database.Options);
        var evicted = await verify.Downloads.ToDictionaryAsync(row => row.ClientIp, row => row.IsEvicted);
        Assert.True(evicted["before"]);
        Assert.False(evicted["after"]);
    }

    [Fact]
    public async Task AClearWhoseMiddleDatasourceFailsStillClearsTheRestThenEndsAmberAsync()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", [], true), ("beta", [], true), ("gamma", [], true));
        var alpha = run.Datasources.GetDatasource("alpha")!.CachePath;
        var beta = run.Datasources.GetDatasource("beta")!.CachePath;
        var gamma = run.Datasources.GetDatasource("gamma")!.CachePath;
        // cache_clear for a root it cannot list: main writes the failed progress, prints the error and exits 1.
        run.Rust.Clears[beta] = (
            JsonSerializer.Serialize(new
            {
                isProcessing = false,
                percentComplete = 0.0,
                status = "failed",
                stageKey = "signalr.cacheClear.error.fatal",
                context = new { errorDetail = "failed to enumerate cache root: Input/output error (os error 5)" },
                directoriesProcessed = 0,
                totalDirectories = 1,
                bytesDeleted = 0,
                filesDeleted = 0,
                activeDirectories = Array.Empty<string>(),
                activeCount = 0,
                timestamp = "2026-10-02T00:00:00Z",
                undeletedFiles = 0
            }),
            1,
            "Error: failed to enumerate cache root: Input/output error (os error 5)");

        var clearId = Assert.IsType<Guid>(await run.Clearing.StartCacheClearAsync());

        var terminal = await WaitForTerminalAsync(run.Tracker, clearId);
        Assert.Equal(OperationStatus.Completed, terminal.Status);
        Assert.Equal([alpha, beta, gamma], run.Rust.ClearedPaths);
        var row = Assert.Single(run.Tracker.GetRuns().Runs, item => item.OperationId == clearId);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("common.notifications.warnings.datasourcesNotCleared", warning.StageKey);
        Assert.Equal("beta", warning.Context["datasources"]);
    }

    [Fact]
    public async Task AClearThatClearedOneDatasourceAndFailedOnAnotherEndsAmberAsync()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", [], true), ("beta", [], true));
        var alpha = run.Datasources.GetDatasource("alpha")!.CachePath;
        var beta = run.Datasources.GetDatasource("beta")!.CachePath;
        // cache_clear that deleted a file in its root: it exits 0 and reports one file deleted.
        run.Rust.Clears[alpha] = (
            JsonSerializer.Serialize(new
            {
                isProcessing = false,
                percentComplete = 100.0,
                status = "completed",
                stageKey = "signalr.cacheClear.progress",
                context = new { processed = 1, totalDirs = 1, activeCount = 0 },
                directoriesProcessed = 1,
                totalDirectories = 1,
                bytesDeleted = 4096,
                filesDeleted = 1,
                activeDirectories = Array.Empty<string>(),
                activeCount = 0,
                timestamp = "2026-10-02T00:00:00Z",
                undeletedFiles = 0
            }),
            0,
            string.Empty);
        // cache_clear for a root it cannot list: main writes the failed progress, prints the error and exits 1.
        run.Rust.Clears[beta] = (
            JsonSerializer.Serialize(new
            {
                isProcessing = false,
                percentComplete = 0.0,
                status = "failed",
                stageKey = "signalr.cacheClear.error.fatal",
                context = new { errorDetail = "failed to enumerate cache root: Input/output error (os error 5)" },
                directoriesProcessed = 0,
                totalDirectories = 1,
                bytesDeleted = 0,
                filesDeleted = 0,
                activeDirectories = Array.Empty<string>(),
                activeCount = 0,
                timestamp = "2026-10-02T00:00:00Z",
                undeletedFiles = 0
            }),
            1,
            "Error: failed to enumerate cache root: Input/output error (os error 5)");

        var clearId = Assert.IsType<Guid>(await run.Clearing.StartCacheClearAsync());

        var terminal = await WaitForTerminalAsync(run.Tracker, clearId);
        Assert.Equal(OperationStatus.Completed, terminal.Status);
        var row = Assert.Single(run.Tracker.GetRuns().Runs, item => item.OperationId == clearId);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("common.notifications.warnings.datasourcesNotCleared", warning.StageKey);
        Assert.Equal("beta", warning.Context["datasources"]);
        // The repair keeps the failed outcome so its full scan decides the datasource that failed.
        var repair = await run.WaitForCompletedRepairAsync(clearId);
        Assert.Equal(OperationStatus.Failed, repair.Outcome);
        // The card ended amber, so a restart during the repair restores it amber too.
        Assert.True(repair.RunCompleted);
    }

    [Fact]
    public async Task APartlyClearedClearSavesItsWarningBeforeItsOutcomeAsync()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", [], true), ("beta", [], true));
        var alpha = run.Datasources.GetDatasource("alpha")!.CachePath;
        var beta = run.Datasources.GetDatasource("beta")!.CachePath;
        run.Rust.Clears[alpha] = (
            JsonSerializer.Serialize(new
            {
                isProcessing = false,
                percentComplete = 100.0,
                status = "completed",
                stageKey = "signalr.cacheClear.progress",
                context = new { processed = 1, totalDirs = 1, activeCount = 0 },
                directoriesProcessed = 1,
                totalDirectories = 1,
                bytesDeleted = 4096,
                filesDeleted = 1,
                activeDirectories = Array.Empty<string>(),
                activeCount = 0,
                timestamp = "2026-10-02T00:00:00Z",
                undeletedFiles = 0
            }),
            0,
            string.Empty);
        run.Rust.Clears[beta] = (
            JsonSerializer.Serialize(new
            {
                isProcessing = false,
                percentComplete = 0.0,
                status = "failed",
                stageKey = "signalr.cacheClear.error.fatal",
                context = new { errorDetail = "failed to enumerate cache root: Input/output error (os error 5)" },
                directoriesProcessed = 0,
                totalDirectories = 1,
                bytesDeleted = 0,
                filesDeleted = 0,
                activeDirectories = Array.Empty<string>(),
                activeCount = 0,
                timestamp = "2026-10-02T00:00:00Z",
                undeletedFiles = 0
            }),
            1,
            "Error: failed to enumerate cache root: Input/output error (os error 5)");
        // Every repair file write is what a restart right after it would read.
        var state = Assert.IsType<OperationRepairTests.FailingStateService>(run.State);
        var writes = new List<List<OperationRepair>>();
        state.OnRepairWrite = contents => writes.Add(JsonSerializer.Deserialize<List<OperationRepair>>(contents)!);

        var clearId = Assert.IsType<Guid>(await run.Clearing.StartCacheClearAsync());

        await run.WaitForCompletedRepairAsync(clearId);
        var firstOutcome = writes
            .SelectMany(repairs => repairs)
            .First(repair => repair.Id == clearId && repair.Outcome is not null);
        Assert.NotNull(firstOutcome.Warnings);
        var warning = Assert.Single(firstOutcome.Warnings);
        Assert.Equal("common.notifications.warnings.datasourcesNotCleared", warning.StageKey);
    }

    [Fact]
    public async Task AClearThatCouldNotDeleteSomeFilesCompletesWithAWarningAsync()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", [], true));
        var alpha = run.Datasources.GetDatasource("alpha")!.CachePath;
        var kept = Path.Combine(alpha, "aa", "0123456789abcdef0123456789abcdef");
        // cache_clear when one file could not be deleted: it clears the rest, exits 0 and names the file
        // in its final progress.
        run.Rust.Clears[alpha] = (
            JsonSerializer.Serialize(new
            {
                isProcessing = false,
                percentComplete = 100.0,
                status = "completed",
                stageKey = "signalr.cacheClear.progress",
                context = new { processed = 1, totalDirs = 1, activeCount = 0 },
                directoriesProcessed = 1,
                totalDirectories = 1,
                bytesDeleted = 4096,
                filesDeleted = 1,
                activeDirectories = Array.Empty<string>(),
                activeCount = 0,
                timestamp = "2026-10-02T00:00:00Z",
                undeletedFiles = 1,
                firstUndeleted = kept
            }),
            0,
            string.Empty);

        var clearId = Assert.IsType<Guid>(await run.Clearing.StartCacheClearAsync());

        var terminal = await WaitForTerminalAsync(run.Tracker, clearId);
        Assert.Equal(OperationStatus.Completed, terminal.Status);
        var row = Assert.Single(run.Tracker.GetRuns().Runs, item => item.OperationId == clearId);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("common.notifications.warnings.cacheFilesKept", warning.StageKey);
        Assert.Equal(1UL, warning.Context["fileCount"]);
        Assert.Equal(kept, warning.Context["path"]);
        Assert.True(row.Retained);
        var repair = await run.WaitForCompletedRepairAsync(clearId);
        Assert.False(Assert.Single(repair.Sources).NativeCompletionAccepted);
        Assert.Equal("common.notifications.warnings.cacheFilesKept", Assert.Single(repair.Warnings!).StageKey);
    }

    [Fact]
    public async Task AClearThatSkippedALinkedFolderWhoseDiskIsGoneEndsAmberNamingItAsync()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", [], true));
        var alpha = run.Datasources.GetDatasource("alpha")!.CachePath;
        var skipped = Path.Combine(alpha, "00");
        // cache_clear for a 2-hex folder linked to a disk that is not mounted: it clears the rest, exits 0 and
        // names the folder in skippedFolders of its final progress.
        run.Rust.Clears[alpha] = (
            JsonSerializer.Serialize(new
            {
                isProcessing = false,
                percentComplete = 100.0,
                status = "completed",
                stageKey = "signalr.cacheClear.progress",
                context = new { processed = 1, totalDirs = 1, activeCount = 0 },
                directoriesProcessed = 1,
                totalDirectories = 1,
                bytesDeleted = 4096,
                filesDeleted = 1,
                activeDirectories = Array.Empty<string>(),
                activeCount = 0,
                timestamp = "2026-10-02T00:00:00Z",
                undeletedFiles = 0,
                skippedFolders = new[] { skipped }
            }),
            0,
            string.Empty);

        var clearId = Assert.IsType<Guid>(await run.Clearing.StartCacheClearAsync());

        var terminal = await WaitForTerminalAsync(run.Tracker, clearId);
        Assert.Equal(OperationStatus.Completed, terminal.Status);
        var row = Assert.Single(run.Tracker.GetRuns().Runs, item => item.OperationId == clearId);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("common.notifications.warnings.cacheFoldersNotCleared", warning.StageKey);
        Assert.Equal(skipped, warning.Context["folders"]);
        Assert.True(row.Retained);
        var repair = await run.WaitForCompletedRepairAsync(clearId);
        Assert.False(Assert.Single(repair.Sources).NativeCompletionAccepted);
    }

    [Fact]
    public async Task AClearOfARootWhoseOnlyFolderIsALinkWhoseDiskIsGoneEndsAmberNamingItAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var run = await RepairRun.CreateAsync(("alpha", [], true));
        var alpha = run.Datasources.GetDatasource("alpha")!.CachePath;
        var skipped = Path.Combine(alpha, "00");
        // The root holds only a 2-hex link whose target is not mounted.
        Directory.Delete(Path.Combine(alpha, "aa"), recursive: true);
        Directory.CreateSymbolicLink(skipped, Path.Combine(run.Root, "unmounted-disk"));
        // cache_clear for that root: it names the link in skippedFolders of its final progress (cache_clear.rs
        // reports a link whose target is gone), clears nothing and exits 0.
        run.Rust.Clears[alpha] = (
            JsonSerializer.Serialize(new
            {
                isProcessing = false,
                percentComplete = 100.0,
                status = "completed",
                stageKey = "signalr.cacheClear.progress",
                context = new { processed = 0, totalDirs = 1, activeCount = 0 },
                directoriesProcessed = 0,
                totalDirectories = 1,
                bytesDeleted = 0,
                filesDeleted = 0,
                activeDirectories = Array.Empty<string>(),
                activeCount = 0,
                timestamp = "2026-10-02T00:00:00Z",
                undeletedFiles = 0,
                skippedFolders = new[] { skipped }
            }),
            0,
            string.Empty);

        var clearId = Assert.IsType<Guid>(await run.Clearing.StartCacheClearAsync());

        var terminal = await WaitForTerminalAsync(run.Tracker, clearId);
        Assert.Equal(OperationStatus.Completed, terminal.Status);
        var row = Assert.Single(run.Tracker.GetRuns().Runs, item => item.OperationId == clearId);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("common.notifications.warnings.cacheFoldersNotCleared", warning.StageKey);
        Assert.Equal(skipped, warning.Context["folders"]);
        Assert.Equal([alpha], run.Rust.ClearedPaths);
    }

    [Fact]
    public async Task XWhileAnEvictionScanPreparesItsRepairEndsItCanceledAsync()
    {
        using var ctx = new PhaseContext();
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var admissionGate = (SemaphoreSlim)typeof(OperationStateService)
            .GetField("_admissionGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(ctx._operationStateService)!;
        // The repair owner is busy admitting another repair, so the scan waits to prepare its own.
        await admissionGate.WaitAsync();
        try
        {
            var scanId = Assert.IsType<Guid>(typeof(CacheReconciliationService)
                .GetMethod("StartScanInBackground", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(ctx.Scan, ["Eviction Scan", new RunNotice(NotificationMode.All, RunTrigger.Manual), null]));

            tracker.CancelOperation(scanId);

            var terminal = await WaitForTerminalAsync(tracker, scanId);
            Assert.Equal(OperationStatus.Cancelled, terminal.Status);
        }
        finally
        {
            admissionGate.Release();
        }
    }

    [Fact]
    public async Task AClearInWhichOneDatasourceFailedDoesNotNameADatasourceThatDeletedNothingAsClearedAsync()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", [], true), ("beta", [], true));
        var alpha = run.Datasources.GetDatasource("alpha")!.CachePath;
        var beta = run.Datasources.GetDatasource("beta")!.CachePath;
        var kept = Path.Combine(beta, "aa", "0123456789abcdef0123456789abcdef");
        // cache_clear for a root it cannot list: main writes the failed progress, prints the error and exits 1.
        run.Rust.Clears[alpha] = (
            JsonSerializer.Serialize(new
            {
                isProcessing = false,
                percentComplete = 0.0,
                status = "failed",
                stageKey = "signalr.cacheClear.error.fatal",
                context = new { errorDetail = "failed to enumerate cache root: Input/output error (os error 5)" },
                directoriesProcessed = 0,
                totalDirectories = 1,
                bytesDeleted = 0,
                filesDeleted = 0,
                activeDirectories = Array.Empty<string>(),
                activeCount = 0,
                timestamp = "2026-10-02T00:00:00Z",
                undeletedFiles = 0
            }),
            1,
            "Error: failed to enumerate cache root: Input/output error (os error 5)");
        // cache_clear when no file could be deleted: it exits 0 with filesDeleted 0 and names the files it kept.
        run.Rust.Clears[beta] = (
            JsonSerializer.Serialize(new
            {
                isProcessing = false,
                percentComplete = 100.0,
                status = "completed",
                stageKey = "signalr.cacheClear.progress",
                context = new { processed = 1, totalDirs = 1, activeCount = 0 },
                directoriesProcessed = 1,
                totalDirectories = 1,
                bytesDeleted = 0,
                filesDeleted = 0,
                activeDirectories = Array.Empty<string>(),
                activeCount = 0,
                timestamp = "2026-10-02T00:00:00Z",
                undeletedFiles = 2,
                firstUndeleted = kept
            }),
            0,
            string.Empty);

        var clearId = Assert.IsType<Guid>(await run.Clearing.StartCacheClearAsync());

        var terminal = await WaitForTerminalAsync(run.Tracker, clearId);
        Assert.Equal(OperationStatus.Failed, terminal.Status);
        Assert.DoesNotContain("after clearing", terminal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClearThatDeletedNoFileFailsNamingTheFilesItKeptAsync()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", [], true));
        var alpha = run.Datasources.GetDatasource("alpha")!.CachePath;
        var kept = Path.Combine(alpha, "aa", "0123456789abcdef0123456789abcdef");
        // cache_clear when no file could be deleted: every folder still counts as processed, it exits 0 and
        // its final progress has filesDeleted 0 and names the files it kept.
        run.Rust.Clears[alpha] = (
            JsonSerializer.Serialize(new
            {
                isProcessing = false,
                percentComplete = 100.0,
                status = "completed",
                stageKey = "signalr.cacheClear.progress",
                context = new { processed = 1, totalDirs = 1, activeCount = 0 },
                directoriesProcessed = 1,
                totalDirectories = 1,
                bytesDeleted = 0,
                filesDeleted = 0,
                activeDirectories = Array.Empty<string>(),
                activeCount = 0,
                timestamp = "2026-10-02T00:00:00Z",
                undeletedFiles = 2,
                firstUndeleted = kept
            }),
            0,
            string.Empty);

        var clearId = Assert.IsType<Guid>(await run.Clearing.StartCacheClearAsync());

        var terminal = await WaitForTerminalAsync(run.Tracker, clearId);
        Assert.Equal(OperationStatus.Failed, terminal.Status);
        Assert.DoesNotContain("after clearing", terminal.Message, StringComparison.Ordinal);
        var row = Assert.Single(run.Tracker.GetRuns().Runs, item => item.OperationId == clearId);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("common.notifications.warnings.cacheFilesNotDeleted", warning.StageKey);
        Assert.Equal(2UL, warning.Context["fileCount"]);
        Assert.Equal(kept, warning.Context["path"]);
    }

    [Fact]
    public async Task ACanceledClearNamesTheDatasourceThatFailedAndTheFilesItKeptAsync()
    {
        await using var run = await RepairRun.CreateAsync(("alpha", [], true), ("beta", [], true), ("gamma", [], true));
        var alpha = run.Datasources.GetDatasource("alpha")!.CachePath;
        var beta = run.Datasources.GetDatasource("beta")!.CachePath;
        var gamma = run.Datasources.GetDatasource("gamma")!.CachePath;
        var kept = Path.Combine(alpha, "aa", "0123456789abcdef0123456789abcdef");
        // cache_clear when one file could not be deleted: it clears the rest, exits 0 and names the file
        // in its final progress.
        run.Rust.Clears[alpha] = (
            JsonSerializer.Serialize(new
            {
                isProcessing = false,
                percentComplete = 100.0,
                status = "completed",
                stageKey = "signalr.cacheClear.progress",
                context = new { processed = 1, totalDirs = 1, activeCount = 0 },
                directoriesProcessed = 1,
                totalDirectories = 1,
                bytesDeleted = 4096,
                filesDeleted = 1,
                activeDirectories = Array.Empty<string>(),
                activeCount = 0,
                timestamp = "2026-10-02T00:00:00Z",
                undeletedFiles = 1,
                firstUndeleted = kept
            }),
            0,
            string.Empty);
        // cache_clear for a root it cannot list: main writes the failed progress, prints the error and exits 1.
        run.Rust.Clears[beta] = (
            JsonSerializer.Serialize(new
            {
                isProcessing = false,
                percentComplete = 0.0,
                status = "failed",
                stageKey = "signalr.cacheClear.error.fatal",
                context = new { errorDetail = "failed to enumerate cache root: Input/output error (os error 5)" },
                directoriesProcessed = 0,
                totalDirectories = 1,
                bytesDeleted = 0,
                filesDeleted = 0,
                activeDirectories = Array.Empty<string>(),
                activeCount = 0,
                timestamp = "2026-10-02T00:00:00Z",
                undeletedFiles = 0
            }),
            1,
            "Error: failed to enumerate cache root: Input/output error (os error 5)");
        run.Rust.OnClear = (path, operationId) =>
        {
            if (path != gamma)
            {
                return Task.CompletedTask;
            }
            // A cancel kills the running cache_clear, and the run sees the cancel as EnsureSuccess reports it.
            run.Tracker.CancelOperation(operationId);
            throw new OperationCanceledException();
        };

        var clearId = Assert.IsType<Guid>(await run.Clearing.StartCacheClearAsync());

        Assert.Equal(OperationStatus.Cancelled, (await WaitForTerminalAsync(run.Tracker, clearId)).Status);
        Assert.Equal([alpha, beta, gamma], run.Rust.ClearedPaths);
        var row = Assert.Single(run.Tracker.GetRuns().Runs, item => item.OperationId == clearId);
        Assert.Equal(2, row.Warnings.Count);
        var failed = Assert.Single(row.Warnings, item => item.StageKey == "common.notifications.warnings.datasourcesNotCleared");
        Assert.Equal("beta", failed.Context["datasources"]);
        var stayed = Assert.Single(row.Warnings, item => item.StageKey == "common.notifications.warnings.cacheFilesNotDeleted");
        Assert.Equal(1UL, stayed.Context["fileCount"]);
        Assert.Equal(kept, stayed.Context["path"]);
        Assert.True(row.Retained);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheScanWorkerEndsItsCardWhileItsRepairRunsOrItsOutcomeSaveRetries(bool outcomeSaveFails)
    {
        using var ctx = new PhaseContext();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((PhaseReconciliation)ctx.Scan).HeldRepair = held;

        var (tracker, scanId) = await StartFailingScanAsync(ctx, () =>
        {
            if (outcomeSaveFails)
            {
                ((OperationRepairTests.FailingStateService)ctx.State).FailRepairStarts = 1;
            }
        });

        Assert.Equal(OperationStatus.Failed, (await WaitForTerminalAsync(tracker, scanId)).Status);
        var pending = Assert.Single(ctx._operationStateService.GetPendingRepairs(), repair => repair.Id == scanId);
        Assert.Equal(outcomeSaveFails ? OperationRepairPhase.Running : OperationRepairPhase.Repairing, pending.Phase);
        held.SetResult();
    }

    [Fact]
    public async Task AScanWhoseKeyEvidenceTurnsMixedDuringItsDetectionOwesNoRepairAsync()
    {
        using var ctx = new PhaseContext();
        var log = new CapturingLogger<CacheReconciliationService>();
        PhaseContext.SetField(ctx.Scan, "_logger", log);
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var launched = false;
        PhaseContext.SetField(ctx.Scan, "_rustProcessHelper", new ScanResultRustProcessHelper((_, _) =>
        {
            launched = true;
            return Task.CompletedTask;
        }));
        var source = Assert.Single(ctx.Datasources.GetDatasources());
        ctx.Notifications.OnSent = (eventName, value) =>
        {
            // A per-service log appears beside access.log while the detection runs; the scan reports its
            // scanning stage right after the detection ends, before it starts any work.
            if (eventName == SignalREvents.EvictionScanProgress
                && value is EvictionScanProgress { StageKey: "signalr.evictionScan.scanning" })
            {
                File.WriteAllText(Path.Combine(source.LogPath, "steam-access.log"), string.Empty);
            }
        };
        var scanId = Assert.IsType<Guid>(typeof(CacheReconciliationService)
            .GetMethod("StartScanInBackground", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Scan, ["Eviction Scan", new RunNotice(NotificationMode.All, RunTrigger.Manual), null]));
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        await ctx.CompleteDetectionAsync(detection.Id);

        await WaitForTerminalAsync(tracker, scanId);

        Assert.False(launched);
        Assert.DoesNotContain(ctx._operationStateService.GetPendingRepairs(), repair => repair.Id == scanId);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("Skipping eviction scan", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, entry => entry.Message.Contains("Error during eviction scan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AScanWhoseKeyEvidenceTurnsMixedBeforeItsLaunchIsRefusedWithAWarningAsync()
    {
        using var ctx = new PhaseContext();
        var log = new CapturingLogger<CacheReconciliationService>();
        PhaseContext.SetField(ctx.Scan, "_logger", log);
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var launched = false;
        PhaseContext.SetField(ctx.Scan, "_rustProcessHelper", new ScanResultRustProcessHelper((_, _) =>
        {
            launched = true;
            return Task.CompletedTask;
        }));
        var source = Assert.Single(ctx.Datasources.GetDatasources());
        var state = Assert.IsType<OperationRepairTests.FailingStateService>(ctx.State);
        var scanId = Assert.IsType<Guid>(typeof(CacheReconciliationService)
            .GetMethod("StartScanInBackground", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Scan, ["Eviction Scan", new RunNotice(NotificationMode.All, RunTrigger.Manual), null]));
        // A per-service log appears beside access.log once the scan records its checkpoint: after the check
        // that follows the detection phase, before the scan reads the key schemes for its launch.
        state.OnRepairWrite = contents =>
        {
            if (JsonSerializer.Deserialize<List<OperationRepair>>(contents)!.Any(repair => repair.EvictionScanId == scanId))
            {
                File.WriteAllText(Path.Combine(source.LogPath, "steam-access.log"), string.Empty);
            }
        };
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        await ctx.CompleteDetectionAsync(detection.Id);

        await WaitForTerminalAsync(tracker, scanId);

        Assert.False(launched);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("Skipping eviction scan", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, entry => entry.Message.Contains("Error during eviction scan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AScanAttemptSavingItsCountsKeepsTheStoredDetectionWarningAsync()
    {
        using var ctx = new PhaseContext();
        var repair = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.EvictionScan,
            Name = "Eviction Scan",
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            Sources = [new OperationRepairSource { Datasource = Assert.Single(ctx.Datasources.GetDatasources()).Name }],
            EvictionScan = new EvictionScanRepair()
        };
        await ctx._operationStateService.PrepareRepairAsync(repair, CancellationToken.None);
        // A prepared record cannot hold counts; the scan starts its work, which moves it to running.
        await ctx._operationStateService.StartWorkAsync(repair.Id, datasource: null, CancellationToken.None);
        // The repair's attempt copies the record before the scan saves its detection warning.
        var attemptCopy = ctx._operationStateService.GetPendingRepairs().Single(pending => pending.Id == repair.Id);
        await ctx._operationStateService.SaveRepairAsync(
            repair.Id,
            current => current.EvictionScan = new EvictionScanRepair { DetectionError = "Probe abstained", UncheckedFolders = ["/cache/missing"] },
            CancellationToken.None);
        var scanId = Guid.NewGuid();
        await using (var seed = ctx.CreateContext())
        {
            // Already finalized, so the attempt only saves the scan's counts.
            seed.EvictionScanCheckpoints.Add(new EvictionScanCheckpoint
            {
                OperationId = scanId,
                Processed = 3,
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                FinalizedAtUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await (Task)typeof(CacheReconciliationService)
            .GetMethod("FinalizeEvictionScanAttemptAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Scan, [attemptCopy, scanId, true, CancellationToken.None, true])!;

        var stored = ctx._operationStateService.GetPendingRepairs().Single(pending => pending.Id == repair.Id);
        Assert.Equal(3, stored.EvictionScan!.Processed);
        Assert.Equal("Probe abstained", stored.EvictionScan.DetectionError);
        Assert.Equal(["/cache/missing"], stored.EvictionScan.UncheckedFolders);
    }

    [Fact]
    public async Task ARestoredScanThatLeftFoldersUncheckedEndsAmberNamingThemAsync()
    {
        await using var run = await RepairRun.CreateAsync(("default", ["access.log"], true));
        var repair = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.EvictionScan,
            Name = "Eviction Scan",
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            Sources = [new OperationRepairSource { Datasource = "default" }],
            EvictionScan = new EvictionScanRepair { Processed = 3, UncheckedFolders = ["/cache/missing"] }
        };

        await run.Scan.RestoreRepairAsync(repair, CancellationToken.None);
        run.Tracker.CompleteOperation(repair.Id, success: true);

        var ended = Assert.Single(run.Tracker.GetRuns().Runs, row => row.OperationId == repair.Id);
        var warning = Assert.Single(ended.Warnings);
        Assert.Equal("common.notifications.warnings.cacheFoldersUnchecked", warning.StageKey);
        Assert.Equal("/cache/missing", warning.Context["folders"]);
        Assert.True(ended.Retained);
    }

    [Fact]
    public async Task ARestoredScanWhoseDetectionFailedEndsAmberWithTheErrorAsync()
    {
        await using var run = await RepairRun.CreateAsync(("default", ["access.log"], true));
        var repair = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.EvictionScan,
            Name = "Eviction Scan",
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            Sources = [new OperationRepairSource { Datasource = "default" }],
            EvictionScan = new EvictionScanRepair { Processed = 3, DetectionError = "detector crashed" }
        };

        await run.Scan.RestoreRepairAsync(repair, CancellationToken.None);
        run.Tracker.CompleteOperation(repair.Id, success: true);

        var ended = Assert.Single(run.Tracker.GetRuns().Runs, row => row.OperationId == repair.Id);
        var warning = Assert.Single(ended.Warnings);
        Assert.Equal("signalr.gameDetect.error.fatal", warning.StageKey);
        Assert.Equal("detector crashed", warning.Context["errorDetail"]);
    }

    [Fact]
    public async Task AFinishedNormalScanLeavesTheCorruptionResultsRemovable()
    {
        OperationRepair scanRepair;
        using (var ctx = new PhaseContext())
        {
            var (tracker, scanId) = await StartFailingScanAsync(ctx, () => { });
            await WaitForTerminalAsync(tracker, scanId);
            scanRepair = ctx.State.LoadOperationRepairs().Single(repair => repair.Id == scanId);
        }
        Assert.NotEmpty(scanRepair.Sources);
        Assert.All(scanRepair.Sources, source => Assert.False(source.InvalidateCorruption));

        await using var database = await TestDatabase.CreateAsync();
        using var harness = new DatabaseReconciliation(database, database.Options);
        var source = scanRepair.Sources[0];
        var scope = new TempDirPathResolver(harness.Root).GetStructuralCorruptionStateScope(
            source.Datasource,
            source.CacheRoot!);
        var finalizedScanId = await AddCheckpointAsync(database, evicted: 0, unEvicted: 0);
        await using (var seed = new AppDbContext(database.Options))
        {
            var corruptionScanId = Guid.NewGuid();
            seed.CachedCorruptionScans.Add(new CachedCorruptionScan
            {
                ScanId = corruptionScanId,
                DetectionMode = CorruptionDetectionMode.RepeatedMiss,
                IsCurrent = true,
                Threshold = 3,
                LookbackDays = 30,
                ContractVersion = CorruptionReport.SupportedContractVersion,
                Status = "completed",
                StartedAtUtc = DateTime.UtcNow.AddSeconds(-1),
                CompletedAtUtc = DateTime.UtcNow
            });
            seed.CachedCorruptionDetections.Add(new CachedCorruptionDetection
            {
                ScanId = corruptionScanId,
                ServiceName = "steam",
                DatasourceName = source.Datasource,
                CorruptedChunkCount = 1,
                CandidatesJson = "[]",
                RemovalAllowed = true,
                LastDetectedUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
            await seed.Database.ExecuteSqlRawAsync(
                "CREATE TABLE structural_namespaces (scope text PRIMARY KEY); INSERT INTO structural_namespaces (scope) VALUES ({0})",
                scope);
        }

        await FinalizeScanAttemptAsync(harness.Scan, scanRepair, finalizedScanId);

        await using var verify = new AppDbContext(database.Options);
        Assert.True((await verify.CachedCorruptionDetections.SingleAsync()).RemovalAllowed);
        Assert.Equal(
            1,
            await verify.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM structural_namespaces").SingleAsync());
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task AFinalizedScanThatMovedEvictionFlagsRefreshesThePrefillBadges(int evicted, int unEvicted)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var harness = new DatabaseReconciliation(database, database.Options);
        PhaseContext.SetField(harness.Scan, "_cacheDetections", new GameCacheDetectionDataService(
            database.Factory,
            NullLogger<GameCacheDetectionDataService>.Instance));
        var scanId = await AddCheckpointAsync(database, evicted, unEvicted);

        await FinalizeScanAttemptAsync(
            harness.Scan,
            new OperationRepair
            {
                Id = Guid.NewGuid(),
                Type = OperationType.EvictionScan,
                Name = "Eviction Scan",
                StartedAt = DateTime.UtcNow.AddMinutes(-3)
            },
            scanId);

        // No prefill row was proven gone, but the picker hides apps whose downloads are evicted.
        Assert.Equal(1, harness.Notifications.Count(SignalREvents.PrefillCacheChanged));
    }

    [Theory]
    [InlineData(OperationType.EvictionScan, true)]
    [InlineData(OperationType.CacheClearing, false)]
    [InlineData(OperationType.ServiceRemoval, false)]
    public async Task OnlyAFinalizedEvictionScanKeepsTheCacheFilesScan(OperationType type, bool kept)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var harness = new DatabaseReconciliation(database, database.Options);
        var scanId = await AddCheckpointAsync(database, evicted: 0, unEvicted: 0);
        var cachedScan = typeof(CacheManagementService)
            .GetField("_cachedCacheScan", BindingFlags.Instance | BindingFlags.NonPublic)!;
        cachedScan.SetValue(harness.CacheFiles, RuntimeHelpers.GetUninitializedObject(cachedScan.FieldType));

        await FinalizeScanAttemptAsync(
            harness.Scan,
            new OperationRepair
            {
                Id = Guid.NewGuid(),
                Type = type,
                Name = type.ToString(),
                StartedAt = DateTime.UtcNow.AddMinutes(-3)
            },
            scanId);

        Assert.Equal(kept, cachedScan.GetValue(harness.CacheFiles) is not null);
    }

    [Fact]
    public async Task TheRepairScanIsNotTiedToTheCardAndListsADatasourceWithoutAKeyRecipeAsUnknown()
    {
        using var ctx = new PhaseContext();
        var source = Assert.Single(ctx.Datasources.GetDatasources());
        var unmappedLogs = Path.Combine(ctx.Root, "unmapped-logs");
        var unmappedCache = Path.Combine(ctx.Root, "unmapped-cache");
        Directory.CreateDirectory(unmappedLogs);
        Directory.CreateDirectory(unmappedCache);
        // An enabled datasource outside the repair whose log folder is empty has no key recipe.
        var datasources = new DatasourceService(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = source.Name,
                ["LanCache:DataSources:0:LogPath"] = source.LogPath,
                ["LanCache:DataSources:0:CachePath"] = source.CachePath,
                ["LanCache:DataSources:0:Enabled"] = "true",
                ["LanCache:DataSources:1:Name"] = "unmapped",
                ["LanCache:DataSources:1:LogPath"] = unmappedLogs,
                ["LanCache:DataSources:1:CachePath"] = unmappedCache,
                ["LanCache:DataSources:1:Enabled"] = "true"
            }).Build(),
            new TempDirPathResolver(ctx.Root),
            NullLogger<DatasourceService>.Instance);
        PhaseContext.SetField(ctx.Scan, "_datasourceService", datasources);
        PhaseContext.SetField(ctx.Scan, "_capabilityService", new DatasourceCapabilityService(datasources));
        var rust = new CapturingRepairScanRust();
        PhaseContext.SetField(ctx.Scan, "_rustProcessHelper", rust);
        var repair = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.CacheClearing,
            Name = "Cache Clearing",
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CacheClearing = new CacheClearingRepair { EntityKey = "all" },
            Sources =
            [
                new OperationRepairSource
                {
                    Datasource = source.Name,
                    CacheRoot = source.CachePath,
                    KeyScheme = LogSourceLayout.LayoutMonolithic,
                    ReconcileCache = true
                }
            ]
        };
        await ctx._operationStateService.PrepareRepairAsync(repair, CancellationToken.None);
        repair.Sources[0].NativeLaunchAuthorized = true;

        var stopped = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctx.Scan.ReconcileRepairAsync(repair, CancellationToken.None));

        Assert.Equal("captured", stopped.Message);
        Assert.Null(rust.OperationId);
        using var listed = JsonDocument.Parse(rust.Datasources);
        var schemes = listed.RootElement.EnumerateArray().ToDictionary(
            entry => entry.GetProperty("name").GetString()!,
            entry => entry.GetProperty("keyScheme").GetString());
        Assert.Equal(LogSourceLayout.LayoutMonolithic, schemes[source.Name]);
        Assert.Equal("unknown", schemes["unmapped"]);
    }

    [Fact]
    public async Task TheScansDetectionPhaseLeavesTheLogsFree()
    {
        using var ctx = new PhaseContext();
        var scanId = ctx.RegisterScan();
        var phase = ctx.RunPhaseAsync(scanId, CancellationToken.None);
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);

        // An import pass takes the logs while the detection runs.
        await using (await ctx._operationStateService
            .LockLogFilesAsync(null, OperationType.LogProcessing, LogFileLockKind.Ingest, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Assert.False(phase.IsCompleted);
        }

        await ctx.CompleteDetectionAsync(detection.Id);
        await phase.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<(UnifiedOperationTracker Tracker, Guid ScanId)> StartFailingScanAsync(
        PhaseContext ctx,
        Action beforeFailure)
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        PhaseContext.SetField(ctx.Scan, "_rustProcessHelper", new ScanResultRustProcessHelper((_, _) =>
        {
            beforeFailure();
            throw new InvalidOperationException("scan failed");
        }));
        var scanId = Assert.IsType<Guid>(typeof(CacheReconciliationService)
            .GetMethod("StartScanInBackground", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Scan, ["Eviction Scan", new RunNotice(NotificationMode.All, RunTrigger.Manual), null]));
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        await ctx.CompleteDetectionAsync(detection.Id);
        return (tracker, scanId);
    }

    private static Task FinalizeScanAttemptAsync(CacheReconciliationService scan, OperationRepair repair, Guid scanId) =>
        (Task)typeof(CacheReconciliationService)
            .GetMethod("FinalizeEvictionScanAttemptAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scan, [repair, scanId, false, CancellationToken.None, true])!;

    private static async Task<Guid> AddCheckpointAsync(TestDatabase database, int evicted, int unEvicted)
    {
        var scanId = Guid.NewGuid();
        await using var seed = new AppDbContext(database.Options);
        seed.EvictionScanCheckpoints.Add(new EvictionScanCheckpoint
        {
            OperationId = scanId,
            Processed = evicted + unEvicted,
            Evicted = evicted,
            UnEvicted = unEvicted,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-2)
        });
        await seed.SaveChangesAsync();
        return scanId;
    }

    private static async Task SeedEvictedDownloadAsync(
        TestDatabase database,
        string datasource,
        string url,
        long? depotId = null)
    {
        await using var seed = new AppDbContext(database.Options);
        var download = ClearedDownload(url, datasource, DateTime.UtcNow.AddHours(-2), isEvicted: true);
        download.DepotId = depotId;
        seed.Downloads.Add(download);
        await seed.SaveChangesAsync();
        seed.LogEntries.Add(new LogEntryRecord
        {
            Timestamp = DateTime.UtcNow,
            ClientIp = download.ClientIp,
            Service = download.Service,
            Method = "GET",
            Url = url,
            StatusCode = 200,
            Datasource = datasource,
            DownloadId = download.Id
        });
        await seed.SaveChangesAsync();
    }

    private static Download ClearedDownload(
        string identity,
        string datasource,
        DateTime endTimeUtc,
        long? gameAppId = null,
        string service = "steam",
        string? gameName = null,
        long bytes = 1024,
        bool isActive = false,
        bool isEvicted = false) => new()
    {
        Service = service,
        ClientIp = identity,
        Datasource = datasource,
        GameAppId = gameAppId,
        GameName = gameAppId is null ? gameName : $"Game {gameAppId}",
        StartTimeUtc = endTimeUtc.AddMinutes(-1),
        EndTimeUtc = endTimeUtc,
        CacheHitBytes = bytes,
        IsActive = isActive,
        IsEvicted = isEvicted
    };

    private static CachedGameDetection CachedGame(long gameAppId, ulong totalSizeBytes) => new()
    {
        GameAppId = gameAppId,
        GameName = $"Game {gameAppId}",
        Service = "steam",
        CacheFilesFound = 1,
        TotalSizeBytes = totalSizeBytes,
        LastDetectedUtc = DateTime.UtcNow,
        CreatedAtUtc = DateTime.UtcNow
    };

    private static CachedServiceDetection CachedService(string name) => new()
    {
        ServiceName = name,
        CacheFilesFound = 3,
        TotalSizeBytes = 4096
    };

    private static void AddPrefillRows(AppDbContext context)
    {
        context.PrefillCachedDepots.Add(new PrefillCachedDepot
        {
            AppId = 730,
            DepotId = 731,
            ManifestId = 12345,
            AppName = "Counter-Strike 2",
            CachedAtUtc = DateTime.UtcNow,
            TotalBytes = 1024
        });
        context.PrefillCachedApps.Add(new PrefillCachedApp
        {
            Platform = PrefillPlatform.Steam,
            AppId = "730",
            AppName = "Counter-Strike 2",
            CachedAtUtc = DateTime.UtcNow
        });
    }

    private static OperationRepair ClearRepair(
        DateTime startedAt,
        OperationStatus outcome,
        params OperationRepairSource[] sources) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.CacheClearing,
        Name = "Cache Clearing",
        StartedAt = startedAt,
        Outcome = outcome,
        CacheClearing = new CacheClearingRepair { EntityKey = "all" },
        Sources = [.. sources]
    };

    private static OperationRepairSource ClearSource(string datasource, string? keyScheme, string? receiptPath) => new()
    {
        Datasource = datasource,
        CacheRoot = Path.Combine(Path.GetTempPath(), "lcm-cleared", datasource),
        KeyScheme = keyScheme,
        ReceiptPath = receiptPath,
        NativeLaunchAuthorized = true,
        ReconcileCache = true,
        RefreshDetection = true,
        InvalidateCorruption = true
    };

    private static async Task<OperationInfo> WaitForTerminalAsync(
        UnifiedOperationTracker tracker,
        Guid operationId,
        Func<string>? diagnostic = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var operation = tracker.GetOperation(operationId);
            Assert.NotNull(operation);
            if (operation.Status.IsTerminal())
            {
                return operation;
            }

            var details = diagnostic is null ? null : diagnostic();
            var message = string.IsNullOrWhiteSpace(details)
                ? $"Operation {operationId} did not reach a terminal state"
                : $"Operation {operationId} did not reach a terminal state{Environment.NewLine}{details}";
            Assert.True(DateTime.UtcNow < deadline, message);
            await Task.Delay(10);
        }
    }

    private static async Task AssertRemovalRowsAsync(TestDatabase database, bool removed)
    {
        await using var context = new AppDbContext(database.Options);
        Assert.Equal(removed ? 1 : 2, await context.Downloads.CountAsync());
        Assert.Equal(removed ? 1 : 2, await context.LogEntries.CountAsync());
        Assert.Equal(removed ? 1 : 2, await context.CachedGameDetections.CountAsync());
        Assert.Equal(removed ? 1 : 2, await context.PrefillCachedApps.CountAsync());
        Assert.True(await context.Downloads.AnyAsync(row => row.GameAppId == 456 && !row.IsEvicted));
        Assert.True(await context.LogEntries.AnyAsync(row => row.Url == "/keep"));
        Assert.True(await context.CachedGameDetections.AnyAsync(row => row.GameAppId == 456 && !row.IsEvicted));
        Assert.True(await context.PrefillCachedApps.AnyAsync(row => row.AppId == "456"));
        Assert.Equal(!removed, await context.Downloads.AnyAsync(row => row.GameAppId == 123 && row.IsEvicted));
        Assert.Equal(!removed, await context.LogEntries.AnyAsync(row => row.Url == "/remove"));
        Assert.Equal(!removed, await context.CachedGameDetections.AnyAsync(row => row.GameAppId == 123 && row.IsEvicted));
        Assert.Equal(!removed, await context.PrefillCachedApps.AnyAsync(row => row.AppId == "123"));
    }

    private sealed class SaveRustProcessHelper : RustProcessHelper
    {
        private readonly string _target;

        public SaveRustProcessHelper(
            string target,
            IPathResolver paths,
            IUnifiedOperationTracker tracker)
            : base(
                NullLogger<RustProcessHelper>.Instance,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                paths,
                tracker)
        {
            _target = target;
        }

        public int Runs { get; private set; }
        public int PublicationCount { get; private set; }
        public string Arguments { get; private set; } = string.Empty;

        public override async Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
            ProcessStartInfo start,
            Guid? operationId,
            CancellationToken cancellationToken,
            Func<RustProgressEvent, Task>? onProgressEvent,
            string processLabel = "rust")
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("cache_purge_log_entries", processLabel);
            Assert.NotNull(operationId);
            Runs++;
            Arguments = start.Arguments;
            var quoted = Regex.Matches(start.Arguments, "\"([^\"]*)\"")
                .Select(match => match.Groups[1].Value)
                .ToArray();
            Assert.True(quoted.Length >= 4, start.Arguments);
            Assert.Equal(Path.GetFullPath(Path.GetDirectoryName(_target)!), Path.GetFullPath(quoted[0]));
            Assert.True(File.Exists(quoted[1]));
            Assert.EndsWith(".json", quoted[2], StringComparison.Ordinal);
            var checkPath = start.Environment["LANCACHE_LOG_CHECK"];
            var resultPath = start.Environment["LANCACHE_LOG_RESULT"];
            Assert.False(string.IsNullOrWhiteSpace(checkPath));
            Assert.False(string.IsNullOrWhiteSpace(resultPath));
            Assert.True(File.Exists(checkPath));

            if (Runs is 1 or 2)
            {
                var check = JsonSerializer.Deserialize<NginxPublicationCheckFile>(
                    await File.ReadAllTextAsync(checkPath, cancellationToken),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var expected = Assert.Single(check!.Files);
                Assert.Equal(Path.GetFullPath(_target), Path.GetFullPath(expected.TargetPath));
                var temporaryPath = _target + ".published";
                await File.WriteAllTextAsync(temporaryPath, "GET /keep HTTP/1.1\n", cancellationToken);
                var temporaryIdentity = NginxWriterProbe.ReadIdentity(temporaryPath);
                File.Move(temporaryPath, _target, overwrite: true);
                var publishedIdentity = NginxWriterProbe.ReadIdentity(_target);
                Assert.Equal(temporaryIdentity, publishedIdentity);
                Assert.NotEqual(expected.OriginalIdentity, publishedIdentity);
                PublicationCount++;
                await File.WriteAllTextAsync(
                    resultPath,
                    JsonSerializer.Serialize(
                        new NginxPublicationResult(
                            true,
                            new[]
                            {
                                new NginxPublicationRecord(
                                    _target,
                                    expected.OriginalIdentity,
                                    temporaryIdentity,
                                    publishedIdentity,
                                    Changed: true,
                                    Deleted: false)
                            }),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    cancellationToken);
            }

            var report = Runs switch
            {
                1 => """
                     {"success":true,"lines_removed":3,"log_lines_removed_by_source":{"access":3},"log_lines_removed_before_position_by_source":{"access":2},"permission_errors":0,"error":null}
                     """,
                2 => """
                     {"success":true,"lines_removed":4,"log_lines_removed_by_source":{"access":3,"steam":1},"log_lines_removed_before_position_by_source":{"access":2,"steam":1},"permission_errors":0,"error":null}
                     """,
                _ => """
                     {"success":true,"lines_removed":0,"log_lines_removed_by_source":{},"log_lines_removed_before_position_by_source":{},"permission_errors":0,"error":null}
                     """
            };
            await File.WriteAllTextAsync(quoted[2], report, cancellationToken);
            return new ProcessExecutionResult { ExitCode = 0 };
        }
    }

    /// <summary>
    /// One docker nginx writer over the test log folder. Every reopen runs <c>onReopen</c>, so a
    /// test can see what had happened by the time nginx reopened. Without <c>reopenAnswers</c>
    /// every reopen succeeds at once; with them each reopen takes the next answer, and the second
    /// reopen first waits for <see cref="ReleaseSecondReopen"/>.
    /// </summary>
    private sealed class SaveNginxLogRotationService : NginxLogRotationService
    {
        private readonly string _logs;
        private readonly Action? _onReopen;
        private readonly Queue<ProcessCommandResult>? _reopenAnswers;

        public SaveNginxLogRotationService(
            string logs,
            TempDirPathResolver paths,
            Action? onReopen = null,
            Queue<ProcessCommandResult>? reopenAnswers = null)
            : base(
                NullLogger<NginxLogRotationService>.Instance,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NginxLogRotation:ContainerName"] = "writer"
                }).Build(),
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                paths,
                TimeProvider.System)
        {
            _logs = logs;
            _onReopen = onReopen;
            _reopenAnswers = reopenAnswers;
        }

        public int SignalCalls { get; private set; }
        /// <summary>Runs while a reopen check looks for the log files' writer.</summary>
        public Action? DuringWriterSearch { get; set; }
        /// <summary>Completes when the second reopen starts, which then waits for <see cref="ReleaseSecondReopen"/>.</summary>
        public TaskCompletionSource SecondReopenReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecondReopen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool CanProbeHostWriters => false;
        protected override bool CanReplaceDockerLogs => true;

        protected override async Task<ProcessCommandResult> RunProcessAsync(
            ProcessStartInfo start,
            string label,
            CancellationToken cancellationToken = default)
        {
            ProcessCommandResult result;
            switch (label)
            {
                case "docker nginx writer list":
                    DuringWriterSearch?.Invoke();
                    result = new ProcessCommandResult { ExitCode = 0, Output = "writer\n" };
                    break;
                case "docker nginx mount inspection":
                    result = new ProcessCommandResult { ExitCode = 0, Output = $"{_logs}|/logs\n" };
                    break;
                case "docker nginx writer identity":
                    result = new ProcessCommandResult { ExitCode = 0, Output = "4242|saved\n" };
                    break;
                case "docker nginx writer ownership":
                    result = new ProcessCommandResult { ExitCode = 0 };
                    break;
                case "docker nginx verified reopen":
                    SignalCalls++;
                    _onReopen?.Invoke();
                    if (_reopenAnswers is null)
                    {
                        result = new ProcessCommandResult { ExitCode = 0 };
                        break;
                    }
                    if (SignalCalls == 2)
                    {
                        SecondReopenReached.SetResult();
                        await ReleaseSecondReopen.Task.WaitAsync(cancellationToken);
                    }
                    result = _reopenAnswers.Dequeue();
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected nginx command: {label} {start.Arguments}");
            }

            return result;
        }
    }

    /// <summary>
    /// Stands in for <c>cache_purge_log_entries</c>: keeps the arguments and the input of every
    /// run, publishes every checked file as unchanged, and writes a report. A <c>cache_cleaner</c>
    /// run ends at once as done.
    /// </summary>
    private sealed class CapturingPurgeRust(IPathResolver paths) : RustProcessHelper(
        NullLogger<RustProcessHelper>.Instance,
        new ProcessManager(NullLogger<ProcessManager>.Instance),
        paths,
        operationTracker: null!)
    {
        public string Arguments { get; private set; } = string.Empty;
        public byte[] Input { get; private set; } = [];
        public string OutputPath { get; private set; } = string.Empty;
        public List<(string Logs, string Input)> Runs { get; } = [];
        /// <summary>Runs once the child has rewritten the log, with its run number from 1 and its operation.</summary>
        public Func<int, Guid, Task>? OnRun { get; set; }
        /// <summary>The permission_errors count each purge report carries, as cache_purge_log_entries writes it.</summary>
        public int PermissionErrors { get; set; }
        /// <summary>The cache paths each cache_cleaner run was given, in order.</summary>
        public List<string> ClearedPaths { get; } = [];
        /// <summary>
        /// What a cache_cleaner run for a listed cache path writes, as cache_clear does: its progress file,
        /// its exit code and its stderr. A path not listed ends at once as done.
        /// </summary>
        public Dictionary<string, (string Progress, int ExitCode, string Error)> Clears { get; } = [];
        /// <summary>Runs when a cache_cleaner run starts, with its cache path and its operation.</summary>
        public Func<string, Guid, Task>? OnClear { get; set; }

        public override async Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
            ProcessStartInfo start,
            Guid? operationId,
            CancellationToken cancellationToken,
            Func<RustProgressEvent, Task>? onProgressEvent,
            string processLabel = "rust")
        {
            if (processLabel == "cache_cleaner")
            {
                var clearArguments = Regex.Matches(start.Arguments, "\"([^\"]*)\"")
                    .Select(match => match.Groups[1].Value)
                    .ToArray();
                ClearedPaths.Add(clearArguments[0]);
                if (OnClear is not null)
                {
                    await OnClear(clearArguments[0], Assert.IsType<Guid>(operationId));
                }
                if (!Clears.TryGetValue(clearArguments[0], out var clear))
                {
                    return new ProcessExecutionResult { ExitCode = 0 };
                }
                await File.WriteAllTextAsync(clearArguments[1], clear.Progress, cancellationToken);
                return new ProcessExecutionResult { ExitCode = clear.ExitCode, Error = clear.Error };
            }

            Assert.Equal("cache_purge_log_entries", processLabel);
            Arguments = start.Arguments;
            var quoted = Regex.Matches(start.Arguments, "\"([^\"]*)\"")
                .Select(match => match.Groups[1].Value)
                .ToArray();
            Input = await File.ReadAllBytesAsync(quoted[1], cancellationToken);
            OutputPath = quoted[2];
            Runs.Add((quoted[0], System.Text.Encoding.UTF8.GetString(Input)));
            var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var check = JsonSerializer.Deserialize<NginxPublicationCheckFile>(
                await File.ReadAllTextAsync(start.Environment["LANCACHE_LOG_CHECK"]!, cancellationToken),
                web)!;
            await File.WriteAllTextAsync(
                start.Environment["LANCACHE_LOG_RESULT"]!,
                JsonSerializer.Serialize(
                    new NginxPublicationResult(
                        true,
                        check.Files
                            .Select(file => new NginxPublicationRecord(
                                file.TargetPath,
                                file.OriginalIdentity,
                                null,
                                file.OriginalIdentity,
                                Changed: false,
                                Deleted: false))
                            .ToArray()),
                    web),
                cancellationToken);
            await File.WriteAllTextAsync(
                OutputPath,
                $$"""
                {"success":true,"lines_removed":2,"log_lines_removed_by_source":{"access":2},"log_lines_removed_before_position_by_source":{"access":1},"permission_errors":{{PermissionErrors}},"error":null}
                """,
                cancellationToken);
            if (OnRun is not null)
            {
                await OnRun(Runs.Count, Assert.IsType<Guid>(operationId));
            }
            return new ProcessExecutionResult { ExitCode = 0 };
        }
    }

    /// <summary>
    /// A <see cref="CacheReconciliationService"/> carrying only the members the cache clear's evict
    /// and the scan finalize read, over a real PostgreSQL schema so their bulk updates run as they
    /// do in production. The scoped context takes its own options so a test can make it retrying.
    /// </summary>
    private sealed class DatabaseReconciliation : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly GameCacheDetectionService _detection;

        public DatabaseReconciliation(TestDatabase database, DbContextOptions<AppDbContext> scopedOptions)
        {
            Root = Path.Combine(Path.GetTempPath(), "lcm-clear-evict", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var paths = new TempDirPathResolver(Root);
            var configuration = new ConfigurationBuilder().Build();
            var datasources = new DatasourceService(configuration, paths, NullLogger<DatasourceService>.Instance);
            Notifications = (RecordingNotifications)DispatchProxy
                .Create<ISignalRNotificationService, RecordingNotifications>();
            var notifications = (ISignalRNotificationService)(object)Notifications;
            _detection = new GameCacheDetectionService(
                NullLogger<GameCacheDetectionService>.Instance,
                paths,
                operationStateService: null!,
                database.Factory,
                new GameCacheDetectionDataService(
                    database.Factory,
                    NullLogger<GameCacheDetectionDataService>.Instance),
                evictedDetectionPreservationService: null!,
                unknownGameResolutionService: null!,
                rustProcessHelper: null!,
                notifications,
                datasources,
                capabilityService: null!,
                operationTracker: null!,
                Idle());
            var registrations = new ServiceCollection();
            registrations.AddScoped(_ => new AppDbContext(scopedOptions));
            registrations.AddSingleton(new CorruptionDetectionService(
                NullLogger<CorruptionDetectionService>.Instance,
                configuration,
                paths,
                rustProcessHelper: null!,
                notifications,
                datasources,
                database.Factory,
                operationStateService: null!,
                operationTracker: null!,
                capabilityService: null!,
                Idle(),
                nginxLogRotationService: null!,
                stateService: null!));
            registrations.AddSingleton(
                (CacheManagementService)RuntimeHelpers.GetUninitializedObject(typeof(CacheManagementService)));
            _services = registrations.BuildServiceProvider();
            Scan = (CacheReconciliationService)RuntimeHelpers.GetUninitializedObject(
                typeof(CacheReconciliationService));
            PhaseContext.SetField(Scan, "_serviceProvider", _services);
            PhaseContext.SetField(Scan, "_logger", NullLogger<CacheReconciliationService>.Instance);
            PhaseContext.SetField(Scan, "_notifications", notifications);
            PhaseContext.SetField(Scan, "_gameCacheDetectionService", _detection);
        }

        public CacheReconciliationService Scan { get; }
        public RecordingNotifications Notifications { get; }
        public string Root { get; }
        public CacheManagementService CacheFiles => _services.GetRequiredService<CacheManagementService>();

        public void Dispose()
        {
            _detection.Dispose();
            _services.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>
    /// The real repair owner, tracker, log lock and cache services over a PostgreSQL schema and a
    /// temporary folder holding one log folder and one cache root per datasource, so a cache clear,
    /// an eviction removal and their repairs run end to end. Only the native children stand in.
    /// </summary>
    private sealed class RepairRun : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly TestHostApplicationLifetime _lifetime;

        private RepairRun(
            string root,
            TestDatabase database,
            ServiceProvider services,
            TestHostApplicationLifetime lifetime,
            StateService state,
            CapturingPurgeRust rust,
            SaveNginxLogRotationService nginx,
            RecordingNotifications notifications)
        {
            Root = root;
            Database = database;
            _services = services;
            _lifetime = lifetime;
            State = state;
            Rust = rust;
            Nginx = nginx;
            Notifications = notifications;
        }

        public string Root { get; }
        public TestDatabase Database { get; }
        public StateService State { get; }
        public CapturingPurgeRust Rust { get; }
        public SaveNginxLogRotationService Nginx { get; }
        public RecordingNotifications Notifications { get; }
        public UnifiedOperationTracker Tracker => _services.GetRequiredService<UnifiedOperationTracker>();
        public OperationStateService Repairs => _services.GetRequiredService<OperationStateService>();
        public CacheReconciliationService Scan => _services.GetRequiredService<CacheReconciliationService>();
        public CacheClearingService Clearing => _services.GetRequiredService<CacheClearingService>();
        public DatasourceService Datasources => _services.GetRequiredService<DatasourceService>();

        public static async Task<RepairRun> CreateAsync(
            params (string Name, string[] LogFiles, bool CacheFiles)[] datasources)
        {
            var root = Path.Combine(Path.GetTempPath(), "lcm-repair-run", Guid.NewGuid().ToString("N"));
            var settings = new Dictionary<string, string?>();
            for (var index = 0; index < datasources.Length; index++)
            {
                var (name, logFiles, cacheFiles) = datasources[index];
                var logs = Path.Combine(root, "logs", name);
                var cache = Path.Combine(root, "cache", name);
                Directory.CreateDirectory(logs);
                Directory.CreateDirectory(cache);
                foreach (var file in logFiles)
                {
                    await File.WriteAllTextAsync(Path.Combine(logs, file), "GET /line HTTP/1.1\n");
                }
                if (cacheFiles)
                {
                    Directory.CreateDirectory(Path.Combine(cache, "aa"));
                    await File.WriteAllTextAsync(Path.Combine(cache, "aa", "0123456789abcdef0123456789abcdef"), "x");
                }
                settings[$"LanCache:DataSources:{index}:Name"] = name;
                settings[$"LanCache:DataSources:{index}:LogPath"] = logs;
                settings[$"LanCache:DataSources:{index}:CachePath"] = cache;
                settings[$"LanCache:DataSources:{index}:Enabled"] = "true";
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var paths = new TempDirPathResolver(root) { DockerSocketAvailable = true };
            // The services refuse to launch a child whose binary is missing.
            foreach (var binary in new[] { paths.GetRustLogPurgePath(), paths.GetRustCacheCleanerPath() })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
                await File.WriteAllTextAsync(binary, string.Empty);
            }

            var database = await TestDatabase.CreateAsync();
            var state = OperationRepairTests.CreateFailingStateService(Path.Combine(root, "state"));
            typeof(StateService)
                .GetField("_cachedState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(state, new AppState());
            var datasourceService = new DatasourceService(configuration, paths, NullLogger<DatasourceService>.Instance);
            var capability = new DatasourceCapabilityService(datasourceService);
            var notificationState = (RecordingNotifications)DispatchProxy
                .Create<ISignalRNotificationService, RecordingNotifications>();
            var notifications = (ISignalRNotificationService)(object)notificationState;
            var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
            var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
            var rust = new CapturingPurgeRust(paths);
            var nginx = new SaveNginxLogRotationService(Path.Combine(root, "logs"), paths);
            var lifetime = new TestHostApplicationLifetime();

            OperationStateService operationState = null!;
            GameCacheDetectionService detection = null!;
            var registrations = new ServiceCollection();
            registrations.AddScoped(_ => new AppDbContext(database.Options));
            registrations.AddSingleton(datasourceService);
            registrations.AddSingleton(capability);
            registrations.AddSingleton(notifications);
            registrations.AddSingleton(tracker);
            registrations.AddSingleton(_ => operationState);
            registrations.AddSingleton(_ => detection);
            registrations.AddSingleton(_ => new CorruptionDetectionService(
                NullLogger<CorruptionDetectionService>.Instance,
                configuration,
                paths,
                rust,
                notifications,
                datasourceService,
                database.Factory,
                operationState,
                tracker,
                capability,
                Idle(),
                nginx,
                state));
            registrations.AddSingleton(_ => new CacheManagementService(
                configuration,
                NullLogger<CacheManagementService>.Instance,
                paths,
                rust,
                nginx,
                datasourceService,
                state,
                database.Factory,
                detection,
                tracker,
                notifications,
                envFileReader: null!,
                new OperationConflictChecker(tracker, operationState, NullLogger<OperationConflictChecker>.Instance),
                capability,
                Idle(),
                operationState));
            registrations.AddSingleton(container => new RustLogProcessorService(
                NullLogger<RustLogProcessorService>.Instance,
                paths,
                notifications,
                state,
                container,
                rust,
                datasourceService,
                tracker));
            registrations.AddSingleton(container => new CacheReconciliationService(
                container,
                NullLogger<CacheReconciliationService>.Instance,
                configuration,
                datasourceService,
                state,
                notifications,
                tracker,
                rust,
                nginx,
                paths,
                new GameCacheDetectionDataService(database.Factory, NullLogger<GameCacheDetectionDataService>.Instance),
                detection,
                evictedDetectionPreservationService: null!,
                operationQueue: null!,
                lifetime,
                capability,
                Idle()));
            registrations.AddSingleton(_ => new CacheClearingService(
                NullLogger<CacheClearingService>.Instance,
                notifications,
                configuration,
                paths,
                state,
                rust,
                datasourceService,
                tracker,
                capability,
                operationState));
            var services = registrations.BuildServiceProvider();
            operationState = new OperationStateService(
                NullLogger<OperationStateService>.Instance,
                configuration,
                state,
                services.GetRequiredService<IServiceScopeFactory>(),
                lifetime,
                processes,
                tracker);
            detection = new GameCacheDetectionService(
                NullLogger<GameCacheDetectionService>.Instance,
                paths,
                operationState,
                database.Factory,
                new GameCacheDetectionDataService(database.Factory, NullLogger<GameCacheDetectionDataService>.Instance),
                evictedDetectionPreservationService: null!,
                unknownGameResolutionService: null!,
                rust,
                notifications,
                datasourceService,
                capability,
                tracker,
                Idle());
            await operationState.StartAsync(CancellationToken.None);
            return new RepairRun(root, database, services, lifetime, state, rust, nginx, notificationState);
        }

        public OperationRepair ReadRepair(Guid operationId) =>
            State.LoadOperationRepairs().Single(repair => repair.Id == operationId);

        public async Task<OperationRepair> WaitForCompletedRepairAsync(Guid operationId)
        {
            await WaitUntilAsync(() => State.LoadOperationRepairs()
                .Any(repair => repair.Id == operationId && repair.Phase == OperationRepairPhase.Completed));
            return ReadRepair(operationId);
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.StopApplication();
            await Repairs.StopAsync(CancellationToken.None);
            await _services.DisposeAsync();
            await Database.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A temp tree the OS still holds open is not this test's concern.
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold within 10 seconds");
            await Task.Delay(10);
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void StopApplication() => _stopping.Cancel();
    }

    /// <summary>
    /// A real <see cref="GameCacheDetectionService"/> so the phase's start call registers a
    /// genuine detection operation, and an uninitialized <see cref="CacheReconciliationService"/>
    /// carrying only the fields the phase reads. The detection's background run fails at once on
    /// its missing database and reports that to the tracker stand-in, which ignores it: the phase
    /// must see the detection as running until the test ends it.
    /// </summary>
    private sealed class PhaseContext : IDisposable
    {
        private readonly string _root;
        private readonly CacheReconciliationService _scan;
        private readonly ServiceProvider _services;
        private readonly PhaseContexts _contexts;
        internal readonly OperationStateService _operationStateService;
        private readonly TestHostApplicationLifetime _lifetime;
        private readonly CapturingLogger<OperationStateService> _repairLog = new();

        public FakeTracker Tracker { get; }
        public GameCacheDetectionService Detection { get; }
        public RecordingNotifications Notifications { get; }
        public StateService State { get; }
        public DatasourceService Datasources { get; }
        public CacheReconciliationService Scan => _scan;
        public string Root => _root;

        public PhaseContext()
        {
            _root = Path.Combine(Path.GetTempPath(), "lcm-cache-scan-phase", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            var pathResolver = new TempDirPathResolver(_root);
            var configuration = new ConfigurationBuilder().Build();
            var apiKeyService = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver);
            var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "dp-keys")));
            var encryption = new SecureStateEncryptionService(
                dataProtection, apiKeyService, NullLogger<SecureStateEncryptionService>.Instance);
            var steamAuthStorage = new SteamAuthStorageService(
                NullLogger<SteamAuthStorageService>.Instance, pathResolver, encryption);
            // Writes like the real one until a test asks a repair write to fail.
            var stateService = new OperationRepairTests.FailingStateService(
                NullLogger<StateService>.Instance, pathResolver, encryption, steamAuthStorage);
            typeof(StateService)
                .GetField("_cachedState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(stateService, new AppState());

            var datasourceService = new DatasourceService(
                configuration, pathResolver, NullLogger<DatasourceService>.Instance);

            // One unambiguous monolithic log source, so the detection's capability gate lets it start.
            var datasource = Assert.Single(datasourceService.GetDatasources());
            Directory.CreateDirectory(datasource.LogPath);
            File.WriteAllText(Path.Combine(datasource.LogPath, "access.log"), string.Empty);

            var capabilityService = new DatasourceCapabilityService(datasourceService);
            var databaseOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase("cache-scan-phase-" + Guid.NewGuid().ToString("N"))
                .Options;
            var contexts = new PhaseContexts(databaseOptions);
            _contexts = contexts;
            var detectionStore = new GameCacheDetectionDataService(
                contexts,
                NullLogger<GameCacheDetectionDataService>.Instance);
            var detectorPath = pathResolver.GetRustGameDetectorPath();
            Directory.CreateDirectory(Path.GetDirectoryName(detectorPath)!);
            File.WriteAllText(detectorPath, string.Empty);
            Directory.CreateDirectory(datasource.CachePath);
            State = stateService;
            Datasources = datasourceService;

            Notifications = (RecordingNotifications)DispatchProxy
                .Create<ISignalRNotificationService, RecordingNotifications>();
            Tracker = (FakeTracker)DispatchProxy
                .Create<IUnifiedOperationTracker, FakeTracker>();
            var detectionRust = new PhaseRust(
                pathResolver,
                (IUnifiedOperationTracker)(object)Tracker);

            OperationStateService operationStateService = null!;
            GameCacheDetectionService detectionService = null!;
            CacheReconciliationService reconciliationService = null!;
            var registrations = new ServiceCollection();
            registrations.AddSingleton(_ => operationStateService);
            registrations.AddSingleton(_ => detectionService);
            registrations.AddSingleton(_ => reconciliationService);
            registrations.AddSingleton(datasourceService);
            registrations.AddSingleton(capabilityService);
            registrations.AddSingleton((ISignalRNotificationService)(object)Notifications);
            registrations.AddScoped(_ => contexts.CreateDbContext());
            // An eviction removal's repair refreshes the per-service log counts.
            registrations.AddSingleton(_ => new CacheManagementService(
                configuration,
                NullLogger<CacheManagementService>.Instance,
                pathResolver,
                detectionRust,
                nginxLogRotationService: null!,
                datasourceService,
                stateService,
                contexts,
                detectionService,
                (IUnifiedOperationTracker)(object)Tracker,
                (ISignalRNotificationService)(object)Notifications,
                envFileReader: null!,
                conflictChecker: null!,
                capabilityService,
                Idle(),
                operationStateService));
            _services = registrations.BuildServiceProvider();
            _lifetime = new TestHostApplicationLifetime();
            _operationStateService = operationStateService = new OperationStateService(
                _repairLog,
                configuration,
                stateService,
                _services.GetRequiredService<IServiceScopeFactory>(),
                _lifetime,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                (IUnifiedOperationTracker)(object)Tracker);

            Detection = detectionService = new GameCacheDetectionService(
                NullLogger<GameCacheDetectionService>.Instance,
                pathResolver,
                operationStateService,
                contexts,
                detectionStore,
                evictedDetectionPreservationService: null!,
                unknownGameResolutionService: null!,
                detectionRust,
                (ISignalRNotificationService)(object)Notifications,
                datasourceService,
                capabilityService,
                (IUnifiedOperationTracker)(object)Tracker,
                Idle());
            _operationStateService.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            _scan = reconciliationService = new PhaseReconciliation(
                _services,
                configuration,
                datasourceService,
                stateService,
                (ISignalRNotificationService)(object)Notifications,
                (IUnifiedOperationTracker)(object)Tracker,
                pathResolver,
                Detection,
                _lifetime,
                capabilityService);
        }

        public Guid RegisterScan(RunNotice? notice = null)
        {
            return (Guid)typeof(CacheReconciliationService).GetMethod("RegisterEvictionScanOperation",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_scan,
                ["Eviction Scan", new CancellationTokenSource(), notice ?? new RunNotice(NotificationMode.All, RunTrigger.Manual)])!;
        }

        public AppDbContext CreateContext() => _contexts.CreateDbContext();

        public Task ReportProgressAsync(Guid id) => (Task)typeof(CacheReconciliationService)
            .GetMethod("ReportScanProgressAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_scan, [id, 0d, "signalr.evictionScan.scanning", new EvictionScanResult()])!;

        public Task RunPhaseAsync(Guid scanOperationId, CancellationToken token)
        {
            var phase = typeof(CacheReconciliationService).GetMethod(
                "RunFullDetectionPhaseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (Task)phase.Invoke(_scan, [scanOperationId, token])!;
        }

        public async Task CompleteDetectionAsync(Guid operationId)
        {
            Tracker.Complete(operationId);
            Tracker.Get(operationId)!.CancellationTokenSource!.Cancel();
            var current = typeof(GameCacheDetectionService)
                .GetField("_currentDetectionTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Detection);
            if (current is not null)
            {
                var worker = (Task)current.GetType().GetField("Item2")!.GetValue(current)!;
                await worker.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public async Task WaitForRepairAsync(Task task, TimeSpan timeout)
        {
            try
            {
                await task.WaitAsync(timeout);
            }
            catch (TimeoutException exception)
            {
                var details = string.Join(
                    Environment.NewLine,
                    _repairLog.Entries
                        .Where(entry => entry.Level >= LogLevel.Error)
                        .Select(entry => $"{entry.Message}: {entry.Exception}"));
                throw new TimeoutException(details, exception);
            }
        }

        public void Dispose()
        {
            Detection.Dispose();
            _lifetime.StopApplication();
            _operationStateService.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _services.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A temp tree the OS still holds open is not this test's concern.
            }
        }

        internal static void SetField(object target, string name, object value)
            => typeof(CacheReconciliationService)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(target, value);
    }

    private sealed class PhaseContexts(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AppDbContext(options));
        }
    }

    private sealed class PhaseRust(
        IPathResolver pathResolver,
        IUnifiedOperationTracker operationTracker)
        : RustProcessHelper(
            NullLogger<RustProcessHelper>.Instance,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            pathResolver,
            operationTracker)
    {
        public override async Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
            ProcessStartInfo process,
            Guid? operationId,
            CancellationToken cancellationToken,
            Func<RustProgressEvent, Task>? onProgressEvent,
            string processLabel = "rust")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ProcessExecutionResult { ExitCode = 0 };
        }
    }

    private sealed class PhaseReconciliation(
        IServiceProvider services,
        IConfiguration configuration,
        DatasourceService datasourceService,
        IStateService stateService,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker,
        IPathResolver pathResolver,
        GameCacheDetectionService gameCacheDetectionService,
        IHostApplicationLifetime applicationLifetime,
        DatasourceCapabilityService capabilityService)
        : CacheReconciliationService(
            services,
            NullLogger<CacheReconciliationService>.Instance,
            configuration,
            datasourceService,
            stateService,
            notifications,
            operationTracker,
            null!,
            null!,
            pathResolver,
            null!,
            gameCacheDetectionService,
            null!,
            null!,
            applicationLifetime,
            capabilityService,
            Idle())
    {
        /// <summary>When set, a repair stays in its resume until the test completes this.</summary>
        public TaskCompletionSource? HeldRepair { get; set; }

        public override async Task ResumeRepairAsync(
            OperationRepair repair,
            CancellationToken stoppingToken)
        {
            stoppingToken.ThrowIfCancellationRequested();
            if (HeldRepair is not null)
            {
                await HeldRepair.Task.WaitAsync(stoppingToken);
            }
        }
    }

    /// <summary>
    /// Captures the process boundary so the test can compare the application operation ID with
    /// the independent database-checkpoint ID passed to the native command.
    /// </summary>
    private sealed class CapturingEvictionRust : RustProcessHelper
    {
        public CapturingEvictionRust(
            ProcessManager processes,
            IPathResolver paths,
            IUnifiedOperationTracker tracker)
            : base(
                NullLogger<RustProcessHelper>.Instance,
                processes,
                paths,
                tracker)
        {
        }

        public Guid? TrackedOperationId { get; private set; }
        public string Arguments { get; private set; } = string.Empty;

        public override Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
            ProcessStartInfo process,
            Guid? operationId,
            CancellationToken cancellationToken,
            Func<RustProgressEvent, Task>? onProgressEvent,
            string processLabel = "rust")
        {
            cancellationToken.ThrowIfCancellationRequested();
            TrackedOperationId = operationId;
            Arguments = process.Arguments;
            return Task.FromResult(new ProcessExecutionResult
            {
                ExitCode = 0,
                Output = "{\"success\":true,\"processed\":0,\"evicted\":0,\"unEvicted\":0}"
            });
        }
    }

    /// <summary>
    /// Stands in for a repair's <c>cache_eviction_scan</c>: keeps the operation it was launched
    /// for and the datasource list it was given, then stops the repair with "captured".
    /// </summary>
    private sealed class CapturingRepairScanRust() : RustProcessHelper(
        NullLogger<RustProcessHelper>.Instance,
        new ProcessManager(NullLogger<ProcessManager>.Instance),
        pathResolver: null!,
        operationTracker: null!)
    {
        public Guid? OperationId { get; private set; }
        public string Datasources { get; private set; } = string.Empty;

        public override async Task<RustExecutionResult> RunEvictionScanAsync(
            string datasourceConfigPath,
            string? progressFile = null,
            CancellationToken cancellationToken = default,
            Guid? operationId = null,
            Func<RustProgressEvent, Task>? onProgressEvent = null,
            Guid? scanId = null,
            string? repairPath = null)
        {
            OperationId = operationId;
            Datasources = await File.ReadAllTextAsync(datasourceConfigPath, cancellationToken);
            throw new InvalidOperationException("captured");
        }
    }

    /// <summary>
    /// The scan's Rust step answering with a finished scan that found nothing newly evicted and
    /// nothing back on disk, so the scan goes on to its Remove-mode cleanup.
    /// </summary>
    private sealed class ScanResultRustProcessHelper : RustProcessHelper
    {
        private readonly Func<Guid, CancellationToken, Task> _complete;
        private readonly string _resultJson;

        public ScanResultRustProcessHelper(
            Func<Guid, CancellationToken, Task> complete,
            string resultJson = """{"success":true,"processed":1,"evicted":0,"unEvicted":0}""")
            : base(
                NullLogger<RustProcessHelper>.Instance,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                pathResolver: null!,
                operationTracker: null!)
        {
            _complete = complete;
            _resultJson = resultJson;
        }

        public override async Task<RustExecutionResult> RunEvictionScanAsync(
            string datasourceConfigPath,
            string? progressFile = null,
            CancellationToken cancellationToken = default,
            Guid? operationId = null,
            Func<RustProgressEvent, Task>? onProgressEvent = null,
            Guid? scanId = null,
            string? repairPath = null)
        {
            await _complete(Assert.IsType<Guid>(scanId), cancellationToken);
            return new RustExecutionResult
            {
                Success = true,
                Data = _resultJson
            };
        }
    }

    private sealed class TempDirPathResolver : PathResolverBase
    {
        private readonly string _basePath;

        public TempDirPathResolver(string basePath) : base(NullLogger.Instance)
        {
            _basePath = basePath;
        }

        protected override string BasePath => _basePath;
        protected override string RustExecutableExtension => string.Empty;
        public bool DockerSocketAvailable { get; init; }

        public override string ResolvePath(string relativePath) => Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.Combine(_basePath, relativePath);
        public override string NormalizePath(string path) => Path.GetFullPath(ResolvePath(path));
        public override bool IsDockerSocketAvailable() => DockerSocketAvailable;
    }

    /// <summary>
    /// Tracker stand-in the test drives. Registration mints a running row; progress lands on it;
    /// terminal subscriptions are kept so <see cref="Complete"/> and a cancel can fire them. The
    /// service's own CompleteOperation call (its background run failing) is ignored on purpose:
    /// the phase must keep waiting until the test ends the detection. Not sealed for DispatchProxy.
    /// </summary>
    public class FakeTracker : DispatchProxy
    {
        private readonly object _sync = new();
        private readonly List<OperationInfo> _operations = [];
        private readonly List<Action<OperationInfo>> _terminalHandlers = [];
        private readonly Dictionary<Guid, Func<OperationTerminalInfo, Task>> _terminalEmits = [];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<Guid> Cancelled { get; } = [];

        internal IReadOnlyList<OperationInfo> Waiting
        {
            get { lock (_sync) return _operations.Where(o => o.Status == OperationStatus.Waiting).ToList(); }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IUnifiedOperationTracker.RegisterOperation):
                {
                    var operation = new OperationInfo
                    {
                        Id = Guid.NewGuid(),
                        Type = (OperationType)args![0]!,
                        Name = (string)args[1]!,
                        Status = (OperationStatus)args[6]!,
                        Metadata = args[3],
                        CancellationTokenSource = (CancellationTokenSource)args[2]!,
                        ParentOperationId = (Guid?)args[7],
                        StartedAt = (DateTime?)args[8] ?? DateTime.UtcNow,
                        Notice = (RunNotice?)args[10],
                        OwnerCompletes = (bool)args[13]!
                    };
                    if (args[5] is Func<OperationTerminalInfo, Task> emit)
                        _terminalEmits[operation.Id] = emit;
                    lock (_sync)
                    {
                        _operations.Add(operation);
                        _changed.TrySetResult();
                        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    return operation.Id;
                }
                case nameof(IUnifiedOperationTracker.GetOperation):
                    lock (_sync)
                    {
                        return _operations.FirstOrDefault(o => o.Id == (Guid)args![0]!);
                    }
                case nameof(IUnifiedOperationTracker.GetActiveOperations):
                    lock (_sync)
                    {
                        return _operations.Where(o => o.Status == OperationStatus.Running &&
                            (args![0] == null || o.Type == (OperationType)args[0]!)).ToList();
                    }
                case nameof(IUnifiedOperationTracker.UpdateProgress):
                    lock (_sync)
                    {
                        var operation = _operations.FirstOrDefault(o => o.Id == (Guid)args![0]!);
                        if (operation != null && !operation.Status.IsTerminal())
                        {
                            operation.PercentComplete = (double)args![1]!;
                            operation.Message = args[2] as string ?? string.Empty;
                            (args[3] as Action<OperationInfo>)?.Invoke(operation);
                        }
                    }
                    return null;
                case nameof(IUnifiedOperationTracker.UpdateMetadata):
                    lock (_sync)
                    {
                        var operation = _operations.FirstOrDefault(o => o.Id == (Guid)args![0]!);
                        if (operation?.Metadata != null && !operation.Status.IsTerminal())
                            ((Action<object>)args![1]!).Invoke(operation.Metadata);
                    }
                    return null;
                case nameof(IUnifiedOperationTracker.CancelOperation):
                {
                    var id = (Guid)args![0]!;
                    lock (_sync)
                    {
                        Cancelled.Add(id);
                    }

                    End(id, OperationStatus.Cancelled);
                    return default(OperationCancelResult);
                }
                case "add_OperationTerminal":
                    lock (_sync)
                    {
                        _terminalHandlers.Add((Action<OperationInfo>)args![0]!);
                    }

                    return null;
                case "remove_OperationTerminal":
                    lock (_sync)
                    {
                        _terminalHandlers.Remove((Action<OperationInfo>)args![0]!);
                    }

                    return null;
                default:
                    return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
            }
        }

        internal async Task<OperationInfo> WaitForOperationAsync(OperationType type)
        {
            while (true)
            {
                Task changed;
                lock (_sync)
                {
                    var found = _operations.FirstOrDefault(o => o.Type == type);
                    if (found != null)
                    {
                        return found;
                    }
                    changed = _changed.Task;
                }

                await changed.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        internal void SetPercent(Guid id, double percent)
        {
            lock (_sync)
            {
                var operation = _operations.FirstOrDefault(o => o.Id == id);
                if (operation != null)
                {
                    operation.PercentComplete = percent;
                }
            }
        }

        internal void Complete(Guid id) => End(id, OperationStatus.Completed);

        internal int Count(OperationType type)
        {
            lock (_sync) return _operations.Count(operation => operation.Type == type);
        }

        internal Task FireTerminal(Guid id, OperationTerminalInfo terminal) => _terminalEmits[id](terminal);
        internal OperationInfo? Get(Guid id) { lock (_sync) return _operations.FirstOrDefault(o => o.Id == id); }
        internal void Fail(Guid id, string error)
        {
            var operation = Get(id)!;
            ((GameDetectionMetrics)operation.Metadata!).Error = error;
            End(id, OperationStatus.Failed);
        }

        private void End(Guid id, OperationStatus status)
        {
            OperationInfo? operation;
            Action<OperationInfo>[] handlers;
            lock (_sync)
            {
                operation = _operations.FirstOrDefault(o => o.Id == id);
                if (operation == null)
                {
                    return;
                }

                if (operation.Status.IsTerminal())
                {
                    return;
                }

                operation.Status = status;
                handlers = _terminalHandlers.ToArray();
            }

            foreach (var handler in handlers)
            {
                handler(operation);
            }
        }
    }

    /// <summary>
    /// Records every broadcast as its event name and payload. Not sealed for DispatchProxy.
    /// </summary>
    public class RecordingNotifications : DispatchProxy
    {
        internal Action<string, object?>? OnSent { get; set; }
        private readonly object _sync = new();
        private readonly List<(string Event, object? Payload)> _sent = [];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is [string eventName, ..])
            {
                OnSent?.Invoke(eventName, args.Length > 1 ? args[1] : null);
                lock (_sync)
                {
                    _sent.Add((eventName, args.Length > 1 ? args[1] : null));
                    _changed.TrySetResult();
                    _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }

        internal async Task<object?> WaitForAsync(string eventName, Func<object?, bool> matches)
        {
            while (true)
            {
                Task changed;
                lock (_sync)
                {
                    var hit = _sent.FirstOrDefault(s => s.Event == eventName && matches(s.Payload));
                    if (hit.Event != null)
                    {
                        return hit.Payload;
                    }
                    changed = _changed.Task;
                }

                await changed.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        internal int Count(string eventName, Func<object?, bool>? matches = null)
        {
            lock (_sync)
            {
                return _sent.Count(item => item.Event == eventName &&
                    (matches is null || matches(item.Payload)));
            }
        }
    }
}
