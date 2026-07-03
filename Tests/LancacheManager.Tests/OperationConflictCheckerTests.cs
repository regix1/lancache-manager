using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public class OperationConflictCheckerTests
{
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
    public async Task Allows_LogRemoval_When_DifferentServiceLogRemoval_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterLogRemoval(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.LogRemoval,
            ConflictScope.Service("epicgames"),
            CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task Allows_ServiceRemoval_When_SameServiceLogRemoval_IsActiveAsync()
    {
        // A log-pipeline op (touches nginx logs) must NOT block a cache ServiceRemoval
        // for the same service - they operate on different resources.
        using var tracker = new TrackerHarness();
        RegisterLogRemoval(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.ServiceRemoval,
            ConflictScope.Service("steam"),
            CancellationToken.None);

        Assert.Null(response);
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

    [Fact]
    public async Task Allows_CorruptionDetectionDetails_When_DifferentServiceDetails_IsActiveAsync()
    {
        // Two per-service "view details" fetches are independent reads and must not conflict -
        // this was the bug behind repeated 409s when browsing different services' corruption details.
        using var tracker = new TrackerHarness();
        RegisterCorruptionDetectionDetails(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.CorruptionDetection,
            ConflictScope.Service("blizzard"),
            CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task Blocks_CorruptionDetectionDetails_When_SameServiceDetails_IsActiveAsync()
    {
        using var tracker = new TrackerHarness();
        RegisterCorruptionDetectionDetails(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.CorruptionDetection,
            ConflictScope.Service("steam"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.duplicate", response!.StageKey);
    }

    [Fact]
    public async Task Blocks_CorruptionDetectionDetails_When_BulkScan_IsActiveAsync()
    {
        // The bulk scan touches every service, so a per-service details fetch must wait for it.
        using var tracker = new TrackerHarness();
        RegisterBulkCorruptionDetection(tracker.Tracker);

        var response = await tracker.Checker.CheckAsync(
            OperationType.CorruptionDetection,
            ConflictScope.Service("steam"),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("errors.conflict.duplicate", response!.StageKey);
    }

    private static void RegisterCorruptionDetectionDetails(IUnifiedOperationTracker tracker, string serviceName)
    {
        tracker.RegisterOperation(
            OperationType.CorruptionDetection,
            $"Corruption Details ({serviceName})",
            new CancellationTokenSource(),
            new CorruptionDetectionMetrics { ServiceName = serviceName });
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

        public TrackerHarness()
        {
            var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
            var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
            Tracker = tracker;
            Checker = new OperationConflictChecker(
                tracker,
                NullLogger<OperationConflictChecker>.Instance);
        }

        public void Dispose()
        {
            foreach (var operation in Tracker.GetActiveOperations())
            {
                Tracker.CompleteOperation(operation.Id, success: false, error: "Disposed test harness");
            }
        }
    }
}
