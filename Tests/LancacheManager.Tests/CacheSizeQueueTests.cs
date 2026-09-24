using System.Runtime.CompilerServices;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class CacheSizeQueueTests
{
    [Fact]
    public async Task ForceFullScan_AlwaysEntersOperationQueueAsync()
    {
        var queuedResponse = new QueuedOperationResponse
        {
            OperationId = Guid.NewGuid(),
            Queued = true,
            Status = "waiting"
        };
        var queue = new RecordingOperationQueue(queuedResponse);

        var result = await CreateController(queue, NotificationMode.All).GetCacheSizeAsync(
            datasource: null,
            force: true,
            CancellationToken.None);

        var accepted = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(202, accepted.StatusCode);
        Assert.Same(queuedResponse, accepted.Value);
        Assert.Equal(OperationType.CacheSizeScan, queue.Type);
        Assert.Equal(ConflictScope.Bulk(), queue.Scope);
        Assert.Equal("Cache File Scan", queue.DisplayName);
        Assert.NotNull(queue.Start);
    }

    // The Storage page's scan button is a person's run of the cache file scan schedule, so it
    // carries that schedule's mode with a manual trigger. [86]
    [Theory]
    [InlineData(NotificationMode.All)]
    [InlineData(NotificationMode.Manual)]
    [InlineData(NotificationMode.Silent)]
    [InlineData(NotificationMode.Hidden)]
    public async Task ForceFullScan_CarriesTheScheduleModeWithAManualTrigger(NotificationMode mode)
    {
        var queue = new RecordingOperationQueue(new QueuedOperationResponse { Status = "started" });

        await CreateController(queue, mode).GetCacheSizeAsync(datasource: null, force: true, CancellationToken.None);

        var notice = Assert.IsType<RunNotice>(queue.Notice);
        Assert.Equal(mode, notice.Mode);
        Assert.Equal(RunTrigger.Manual, notice.Trigger);
    }

    private static CacheController CreateController(IOperationQueue queue, NotificationMode mode)
    {
        // The button reads only the schedule's effective mode, so the service needs no loop behind it.
        var cacheSizeScan = (CacheSizeScanScheduledService)RuntimeHelpers.GetUninitializedObject(typeof(CacheSizeScanScheduledService));
        cacheSizeScan.SetNotificationMode(mode);
        return new CacheController(
            cacheService: null!,
            cacheClearingService: null!,
            corruptionDetectionService: null!,
            logger: NullLogger<CacheController>.Instance,
            pathResolver: null!,
            notifications: null!,
            rustProcessHelper: null!,
            nginxLogRotationService: null!,
            operationTracker: null!,
            datasourceService: null!,
            dbContextFactory: null!,
            reconciliationService: null!,
            conflictChecker: null!,
            operationQueue: queue,
            capabilityService: null!,
            stateService: null!,
            cacheScanGate: CacheScanGateHarness.Idle(),
            cacheSizeScan: cacheSizeScan);
    }

    private sealed class RecordingOperationQueue(QueuedOperationResponse response) : IOperationQueue
    {
        public OperationType? Type { get; private set; }
        public ConflictScope? Scope { get; private set; }
        public string? DisplayName { get; private set; }
        public Func<Task<Guid?>>? Start { get; private set; }
        public RunNotice? Notice { get; private set; }

        public Task<QueuedOperationResponse> EnqueueAsync(
            OperationType type,
            ConflictScope scope,
            string displayName,
            Func<Task<Guid?>> start,
            CancellationToken ct,
            bool reportRefusal = false,
            RunNotice? notice = null)
        {
            Type = type;
            Scope = scope;
            DisplayName = displayName;
            Start = start;
            Notice = notice;
            return Task.FromResult(response);
        }
    }
}
