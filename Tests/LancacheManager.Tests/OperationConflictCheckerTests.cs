using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public class OperationConflictCheckerTests
{
    [Fact]
    public async Task Blocks_ServiceRemoval_When_ServiceScopedEvictionRemoval_IsActive_ForSameService()
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
    public async Task Blocks_CorruptionRemoval_When_ServiceRemoval_IsActive_ForSameService()
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
    public async Task Allows_ServiceScopedCrossTypeRemoval_When_ServiceDiffers()
    {
        using var tracker = new TrackerHarness();
        RegisterServiceRemoval(tracker.Tracker, serviceName: "steam");

        var response = await tracker.Checker.CheckAsync(
            OperationType.CorruptionRemoval,
            ConflictScope.Service("epicgames"),
            CancellationToken.None);

        Assert.Null(response);
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
            var tracker = new UnifiedOperationTracker(NullLogger<UnifiedOperationTracker>.Instance);
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
