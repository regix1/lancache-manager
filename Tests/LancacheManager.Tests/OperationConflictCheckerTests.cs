using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public class OperationConflictCheckerTests
{


    [Fact]
    public async Task ExpectedConflict_IsLoggedAtDebug_NotInformationAsync()
    {
        var logger = new CapturingLogger<OperationConflictChecker>();
        using var tracker = new TrackerHarness(logger);
        RegisterBulkOperation(tracker.Tracker, OperationType.CacheSizeScan, "Cache Size Scan");

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal([LogLevel.Debug], logger.Levels);
    }

    [Fact]
    public async Task Blocks_ServiceRemoval_When_ServiceScopedEvictionRemoval_IsActive_ForSameServiceAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterEvictionRemoval(tracker.Tracker, scope: "service", key: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.ServiceRemoval,
            ConflictScope.Service("steam"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.serviceWideActive", response!.StageKey);
        Assert.Equal("service:steam", response.ActiveOperationScope);
        Assert.Equal(nameof(OperationType.EvictionRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Blocks_CorruptionRemoval_When_ServiceRemoval_IsActive_ForSameServiceAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterServiceRemoval(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.CorruptionRemoval,
            ConflictScope.Service("steam"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.serviceWideActive", response!.StageKey);
        Assert.Equal("service:steam", response.ActiveOperationScope);
        Assert.Equal(nameof(OperationType.ServiceRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Allows_ServiceScopedCrossTypeRemoval_When_ServiceDiffersAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterServiceRemoval(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.CorruptionRemoval,
            ConflictScope.Service("epicgames"),
            CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task Blocks_LogRemoval_When_SameServiceLogRemoval_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterLogRemoval(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogRemoval,
            ConflictScope.Service("steam"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.duplicate", response!.StageKey);
        Assert.Equal("service:steam", response.ActiveOperationScope);
        Assert.Equal(nameof(OperationType.LogRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Blocks_LogRemoval_When_DifferentServiceLogRemoval_IsActiveAsync()
    {
        // Heavy data ops run one at a time: both log removals rewrite the same access.log,
        // so the second one queues instead of running concurrently.
        using var tracker = new TrackerHarness();
        RegisterLogRemoval(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogRemoval,
            ConflictScope.Service("epicgames"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
        Assert.Equal(nameof(OperationType.LogRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Blocks_LogProcessing_When_BulkCorruptionScan_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterBulkCorruptionDetection(tracker.Tracker);

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
        Assert.Equal(nameof(OperationType.CorruptionDetection), response.ActiveOperationType);
    }

    [Fact]
    public async Task Blocks_GameDetection_When_LogProcessing_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterBulkOperation(tracker.Tracker, OperationType.LogProcessing, "Log Processing");

        var response = await tracker.Checker.CheckAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
        Assert.Equal(nameof(OperationType.LogProcessing), response.ActiveOperationType);
    }

    [Fact]
    public async Task Blocks_LogRemoval_When_LogProcessing_IsActiveAsync()
    {
        // Log removal rewrites access.log while log processing reads it - never concurrent.
        using var tracker = new TrackerHarness();
        RegisterBulkOperation(tracker.Tracker, OperationType.LogProcessing, "Log Processing");

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogRemoval,
            ConflictScope.Service("steam"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
    }

    [Fact]
    public async Task Blocks_CacheSizeScan_When_EvictionScan_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterBulkOperation(tracker.Tracker, OperationType.EvictionScan, "Eviction Scan");

        var response = await tracker.Checker.CheckAsync(
            OperationType.CacheSizeScan,
            ConflictScope.Bulk(),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
    }

    [Fact]
    public async Task Duplicate_LogProcessing_Reports_Duplicate_Not_HeavyAsync()
    {
        // The queue relies on the "duplicate" stage key to idempotently return the active op
        // instead of parking a second copy - the heavy section must preserve it.
        using var tracker = new TrackerHarness();
        RegisterBulkOperation(tracker.Tracker, OperationType.LogProcessing, "Log Processing");

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.duplicate", response!.StageKey);
    }


    [Fact]
    public async Task Blocks_ServiceRemoval_When_LogRemoval_IsActiveAsync()
    {
        // Service removal rewrites access.log to prune the service's log lines - the same
        // file the active log removal is rewriting - so it queues instead of racing it.
        using var tracker = new TrackerHarness();
        RegisterLogRemoval(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.ServiceRemoval,
            ConflictScope.Service("steam"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
        Assert.Equal(nameof(OperationType.LogRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Blocks_LogRemoval_When_GameRemoval_IsActiveAsync()
    {
        // The game removal's Rust worker rewrites access.log too (prunes the game's lines),
        // so a new log removal must queue behind it, not silently stall on the internal lock.
        using var tracker = new TrackerHarness();
        RegisterNamedGameRemoval(tracker.Tracker, service: "blizzard", gameName: "Diablo IV");

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogRemoval,
            ConflictScope.Service("teso"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
        Assert.Equal(nameof(OperationType.GameRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Blocks_GameRemoval_When_LogProcessing_IsActiveAsync()
    {
        // Log processing reads access.log from a saved position; a removal shrinking the file
        // underneath it corrupts the position, so the removal queues.
        using var tracker = new TrackerHarness();
        RegisterBulkOperation(tracker.Tracker, OperationType.LogProcessing, "Log Processing");

        var response = await tracker.Checker.CheckAsync(
            OperationType.GameRemoval,
            ConflictScope.NamedGame("blizzard", "Diablo IV"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.heavyOperationActive", response!.StageKey);
    }

    [Fact]
    public async Task Blocks_NamedGameRemoval_When_ServiceRemoval_IsActive_ForSameServiceAsync()
    {
        // A service-wide removal for "blizzard" must cover (block) a named Blizzard game removal.
        using var tracker = new TrackerHarness();
        RegisterServiceRemoval(tracker.Tracker, serviceName: "blizzard");

        var response = await tracker.Checker.CheckAsync(
            OperationType.GameRemoval,
            ConflictScope.NamedGame("blizzard", "Diablo IV"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.serviceWideActive", response!.StageKey);
        Assert.Equal("service:blizzard", response.ActiveOperationScope);
        Assert.Equal(nameof(OperationType.ServiceRemoval), response.ActiveOperationType);
    }

    [Fact]
    public async Task Allows_NamedGameRemoval_When_ServiceRemoval_IsActive_ForDifferentServiceAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterServiceRemoval(tracker.Tracker, serviceName: "riot");

        var response = await tracker.Checker.CheckAsync(
            OperationType.GameRemoval,
            ConflictScope.NamedGame("blizzard", "Diablo IV"),
            CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task Blocks_NamedGameRemoval_When_SameNamedGameRemoval_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterNamedGameRemoval(tracker.Tracker, service: "blizzard", gameName: "Diablo IV");

        var response = await tracker.Checker.CheckAsync(
            OperationType.GameRemoval,
            ConflictScope.NamedGame("blizzard", "Diablo IV"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.duplicate", response!.StageKey);
    }

    [Fact]
    public async Task Allows_NamedGameRemoval_When_DifferentNamedGameRemoval_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterNamedGameRemoval(tracker.Tracker, service: "blizzard", gameName: "Diablo IV");

        var response = await tracker.Checker.CheckAsync(
            OperationType.GameRemoval,
            ConflictScope.NamedGame("blizzard", "Overwatch"),
            CancellationToken.None);

        Assert.Null(response);
    }




    private static void RegisterBulkOperation(IUnifiedOperationTracker tracker, OperationType type, string name)
    {
        // Bulk-scope heavy ops (LogProcessing / GameDetection / EvictionScan / CacheSizeScan)
        // register without metadata, so DeriveScope falls back to Bulk().
        tracker.RegisterOperation(type, name, new CancellationTokenSource());
    }

    private static void RegisterBulkCorruptionDetection(IUnifiedOperationTracker tracker)
    {
        tracker.RegisterOperation(
            OperationType.CorruptionDetection,
            "Corruption Detection",
            new CancellationTokenSource(),
            new CorruptionDetectionMetrics());
    }

    private static void RegisterNamedGameRemoval(IUnifiedOperationTracker tracker, string service, string gameName)
    {
        // Mirrors GamesController.RemoveNamedGameFromCacheAsync: entityKind "named",
        // entityKey "{service}:{gameName}".
        tracker.RegisterOperation(
            OperationType.GameRemoval,
            $"Game Removal: {gameName}",
            new CancellationTokenSource(),
            new RemovalMetrics
            {
                EntityKind = "named",
                EntityKey = $"{service}:{gameName}",
                EntityName = gameName
            });
    }

    private static void RegisterLogRemoval(IUnifiedOperationTracker tracker, string serviceName)
    {
        tracker.RegisterOperation(
            OperationType.LogRemoval,
            "Log Removal",
            new CancellationTokenSource(),
            new RemovalMetrics
            {
                EntityKind = "service",
                EntityKey = serviceName.ToLowerInvariant(),
                EntityName = serviceName
            });
    }

    private static void RegisterServiceRemoval(IUnifiedOperationTracker tracker, string serviceName)
    {
        tracker.RegisterOperation(
            OperationType.ServiceRemoval,
            $"Service removal: {serviceName}",
            new CancellationTokenSource(),
            new RemovalMetrics
            {
                EntityKey = serviceName.ToLowerInvariant(),
                EntityName = serviceName
            });
    }

    private static void RegisterEvictionRemoval(
        IUnifiedOperationTracker tracker,
        string scope,
        string key)
    {
        tracker.RegisterOperation(
            OperationType.EvictionRemoval,
            $"Eviction removal: {scope}:{key}",
            new CancellationTokenSource(),
            new EvictionRemovalMetadata
            {
                Scope = scope,
                Key = key
            });
    }

    private sealed class TrackerHarness : IDisposable
    {
        public IUnifiedOperationTracker Tracker { get; }

        public OperationConflictChecker Checker { get; }

        public TrackerHarness(ILogger<OperationConflictChecker>? logger = null)
        {
            var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
            var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
            Tracker = tracker;
            Checker = new OperationConflictChecker(
                tracker,
                logger ?? NullLogger<OperationConflictChecker>.Instance);
        }

        public void Dispose()
        {
            foreach (var operation in Tracker.GetActiveOperations())
            {
                Tracker.CompleteOperation(operation.Id, success: false, error: "Disposed test harness");
            }
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
        }
    }
}
