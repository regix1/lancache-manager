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
        tracker.CompleteOperation(scanId, success: true);
        await promoted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(childTerminal.Task, scanTerminal.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, terminals.Count(id => id == removalId));
        Assert.Equal(1, terminals.Count(id => id == scanId));
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
        var nginx = new SaveNginxLogRotationService(source.LogPath, paths);
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
            gate);
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
            ctx.State);
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
            gate,
            speedTracker)
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

            var originalIdentity = NginxWriterProbe.ReadIdentity(target);
            var firstLogStart = purgeLog.Entries.Count;
            var firstId = await SaveAsync();
            var first = await WaitForTerminalAsync(tracker, firstId, RepairErrors);
            Assert.Equal(OperationStatus.Failed, first.Status);
            var firstWarnings = purgeLog.Entries
                .Skip(firstLogStart)
                .Where(entry => entry.Level == LogLevel.Warning && entry.Exception is not null)
                .ToList();
            var firstWarning = Assert.Single(firstWarnings);
            var firstError = Assert.IsType<InvalidOperationException>(firstWarning.Exception);
            Assert.Contains("first reopen denied", firstError.Message, StringComparison.Ordinal);
            Assert.Equal(1, nginx.SignalCalls);
            Assert.True(rust.Runs == 1, first.Message);
            Assert.True(rust.PublicationCount == 1, rust.Arguments);
            Assert.NotEqual(originalIdentity, NginxWriterProbe.ReadIdentity(target));
            Assert.DoesNotContain("/remove", await File.ReadAllTextAsync(target), StringComparison.Ordinal);
            Assert.Contains("/keep", await File.ReadAllTextAsync(target), StringComparison.Ordinal);
            Assert.Empty(ctx.State.GetLogSourcePositions(source.Name));
            Assert.Equal(0, ctx.State.GetLogTotalLines(source.Name));
            await AssertRemovalRowsAsync(database, removed: false);

            var secondLogStart = purgeLog.Entries.Count;
            var secondId = await SaveAsync();
            var second = await WaitForTerminalAsync(tracker, secondId, RepairErrors);
            Assert.Equal(OperationStatus.Failed, second.Status);
            var secondWarnings = purgeLog.Entries
                .Skip(secondLogStart)
                .Where(entry => entry.Level == LogLevel.Warning && entry.Exception is not null)
                .ToList();
            var secondWarning = Assert.Single(secondWarnings);
            var secondError = Assert.IsType<InvalidOperationException>(secondWarning.Exception);
            Assert.Contains("second reopen denied", secondError.Message, StringComparison.Ordinal);
            Assert.Equal(2, nginx.SignalCalls);
            Assert.Empty(ctx.State.GetLogSourcePositions(source.Name));
            Assert.Equal(0, ctx.State.GetLogTotalLines(source.Name));
            await AssertRemovalRowsAsync(database, removed: false);

            ctx.State.SetLogSourcePositions(source.Name, new Dictionary<string, long>
            {
                ["access"] = 10,
                ["steam"] = 7
            });
            ctx.State.SetLogTotalLines(source.Name, 20);
            var thirdLogStart = purgeLog.Entries.Count;
            var thirdId = await SaveAsync();
            var third = await WaitForTerminalAsync(tracker, thirdId, RepairErrors);
            Assert.True(third.Status == OperationStatus.Completed, third.Message);
            Assert.Equal(OperationStatus.Completed, third.Status);
            Assert.DoesNotContain(
                purgeLog.Entries.Skip(thirdLogStart),
                entry => entry.Level == LogLevel.Warning && entry.Exception is not null);
            Assert.Equal(3, nginx.SignalCalls);
            var positions = ctx.State.GetLogSourcePositions(source.Name);
            Assert.Equal(8, positions["access"]);
            Assert.Equal(6, positions["steam"]);
            Assert.Equal(14, ctx.State.GetLogPosition(source.Name));
            Assert.Equal(16, ctx.State.GetLogTotalLines(source.Name));
            await AssertRemovalRowsAsync(database, removed: true);

            Assert.Equal(3, rust.Runs);
            Assert.Equal(2, rust.PublicationCount);
            Assert.Equal(3, nginx.SignalCalls);
            foreach (var operationId in new[] { firstId, secondId, thirdId })
            {
                Assert.Equal(1, ctx.Notifications.Count(
                    SignalREvents.EvictionRemovalStarted,
                    value => value is EvictionRemovalStarted started && started.OperationId == operationId));
                Assert.Equal(1, ctx.Notifications.Count(
                    SignalREvents.EvictionRemovalComplete,
                    value => value is EvictionRemovalComplete complete && complete.OperationId == operationId));
            }
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
        var schedules = new ServiceScheduleRegistry([], VisibleClientsStateService(),
            DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>(),
            ScheduleExecutionTestService.Create(), tracker);
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        typeof(ServiceScheduleRegistry)
            .GetMethod(acknowledged ? "AcknowledgeRun" : "HoldRefusedRun", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(schedules, acknowledged
                ? [evictionKey, OperationType.EvictionScan, notice, false]
                : [evictionKey, OperationType.EvictionScan, notice]);
        var heldId = notice.PendingId!.Value;
        var holds = (Dictionary<string, (Guid Id, RunNotice Notice)>)typeof(ServiceScheduleRegistry)
            .GetField("_deferredRuns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(schedules)!;
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
        while (true)
        {
            lock (holds)
            {
                if (!holds.ContainsKey(evictionKey)) break;
            }
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
        Assert.Equal("Cache index could not be read", row.Warning);
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
        Assert.Equal(detectionFailed ? "Cache index could not be read" : null, row.Warning);
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
            .Invoke(ctx.Scan, [context, scanId, CancellationToken.None, scanNotice, false])!;
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

            if (Runs is 1 or 3)
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
                3 => """
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

    private sealed class SaveNginxLogRotationService : NginxLogRotationService
    {
        private readonly string _logs;
        private readonly Queue<ProcessCommandResult> _signals = new(
        [
            new ProcessCommandResult { ExitCode = 41, Error = "first reopen denied" },
            new ProcessCommandResult { ExitCode = 42, Error = "second reopen denied" },
            new ProcessCommandResult { ExitCode = 0 }
        ]);

        public SaveNginxLogRotationService(string logs, TempDirPathResolver paths)
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
        }

        public int SignalCalls { get; private set; }
        protected override bool CanProbeHostWriters => false;
        protected override bool CanReplaceDockerLogs => true;

        protected override Task<ProcessCommandResult> RunProcessAsync(
            ProcessStartInfo start,
            string label)
        {
            ProcessCommandResult result;
            switch (label)
            {
                case "docker nginx writer list":
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
                    result = _signals.Dequeue();
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected nginx command: {label} {start.Arguments}");
            }

            return Task.FromResult(result);
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
            var stateService = new StateService(
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
        public override Task ResumeRepairAsync(
            OperationRepair repair,
            CancellationToken stoppingToken)
        {
            stoppingToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
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
    /// The scan's Rust step answering with a finished scan that found nothing newly evicted and
    /// nothing back on disk, so the scan goes on to its Remove-mode cleanup.
    /// </summary>
    private sealed class ScanResultRustProcessHelper : RustProcessHelper
    {
        private readonly Func<Guid, CancellationToken, Task> _complete;

        public ScanResultRustProcessHelper(Func<Guid, CancellationToken, Task> complete)
            : base(
                NullLogger<RustProcessHelper>.Instance,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                pathResolver: null!,
                operationTracker: null!)
        {
            _complete = complete;
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
                Data = """{"success":true,"processed":1,"evicted":0,"unEvicted":0}"""
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
