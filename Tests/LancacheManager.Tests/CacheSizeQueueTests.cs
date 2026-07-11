using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class CacheSizeQueueTests
{
    [Fact]
    public async Task ForceFullScan_WhenHeavyOperationIsActive_EnqueuesCacheSizeScanAsync()
    {
        var blockerId = Guid.NewGuid();
        var conflictChecker = new FixedConflictChecker(new OperationConflictResponse
        {
            StageKey = "errors.conflict.heavyOperationActive",
            ActiveOperationId = blockerId,
            ActiveOperationType = nameof(OperationType.GameDetection),
            ActiveOperationScope = "bulk"
        });
        var queuedResponse = new QueuedOperationResponse
        {
            OperationId = Guid.NewGuid(),
            Queued = true,
            Status = "waiting"
        };
        var queue = new RecordingOperationQueue(queuedResponse);
        var controller = new CacheController(
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
            conflictChecker,
            queue);

        var result = await controller.GetCacheSizeAsync(
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
        Assert.Equal(1, conflictChecker.CallCount);
    }

    private sealed class FixedConflictChecker(OperationConflictResponse response) : IOperationConflictChecker
    {
        public int CallCount { get; private set; }

        public Task<OperationConflictResponse?> CheckAsync(
            OperationType newType,
            ConflictScope newScope,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<OperationConflictResponse?>(response);
        }
    }

    private sealed class RecordingOperationQueue(QueuedOperationResponse response) : IOperationQueue
    {
        public OperationType? Type { get; private set; }
        public ConflictScope? Scope { get; private set; }
        public string? DisplayName { get; private set; }
        public Func<Task<Guid?>>? Start { get; private set; }

        public Task<QueuedOperationResponse> EnqueueAsync(
            OperationType type,
            ConflictScope scope,
            string displayName,
            Func<Task<Guid?>> start,
            CancellationToken ct)
        {
            Type = type;
            Scope = scope;
            DisplayName = displayName;
            Start = start;
            return Task.FromResult(response);
        }
    }
}
