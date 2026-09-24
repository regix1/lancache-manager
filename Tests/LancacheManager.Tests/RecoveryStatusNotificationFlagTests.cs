using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Tests;

/// <summary>
/// Pins the recovery-status contract for eviction scans and cache-file scans: the status endpoints
/// report the running scan's progress and context, and the run's row decides how it is drawn.
/// </summary>
public class RecoveryStatusNotificationFlagTests
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvictionStatusRetainsNonfatalDetectionFailure(bool failedDetection)
    {
        var context = new Dictionary<string, object?> { ["totalProcessed"] = 12 };
        if (failedDetection) context["detectionError"] = "Cache index could not be read";
        var operation = new OperationInfo
        {
            Id = Guid.NewGuid(), Name = "Eviction Scan", Type = OperationType.EvictionScan, Status = OperationStatus.Running,
            Message = "signalr.evictionScan.scanning", PercentComplete = 25,
            Metadata = new Dictionary<string, object?> { ["context"] = context }
        };
        var service = (CacheReconciliationService)RuntimeHelpers.GetUninitializedObject(typeof(CacheReconciliationService));
        var tracker = (StubOperationTracker)DispatchProxy.Create<IUnifiedOperationTracker, StubOperationTracker>();
        tracker.ActiveOperations = [operation];
        var controller = (StatsController)RuntimeHelpers.GetUninitializedObject(typeof(StatsController));
        SetPrivateField(controller, "_reconciliationService", service);
        SetPrivateField(controller, "_operationTracker", (IUnifiedOperationTracker)(object)tracker);
        var response = Assert.IsType<EvictionScanStatusResponse>(Assert.IsType<OkObjectResult>(controller.EvictionScanStatus().Result).Value);
        Assert.Equal(OperationStatus.Running, response.Status);
        Assert.Equal(operation.Id, response.OperationId);
        var wire = JsonSerializer.SerializeToElement(response, WireOptions);
        Assert.Equal(failedDetection, wire.GetProperty("context").TryGetProperty("detectionError", out _));
    }

    // ---- Cache-file scan: GET /api/cache/size/scan/status (anonymous body from the controller) ----

    [Fact]
    public void CacheSizeScanStatus_ActiveRun_ReportsItsProgress()
    {
        var activeScan = new OperationInfo
        {
            Id = Guid.NewGuid(),
            Type = OperationType.CacheSizeScan,
            Name = "Cache File Scan",
            Status = OperationStatus.Running,
            Message = "signalr.cacheSizeScan.scanning",
            PercentComplete = 42
        };

        var body = InvokeCacheSizeScanStatus(BuildCacheController([activeScan]));
        var wire = JsonSerializer.SerializeToElement(body, WireOptions);
        Assert.True(wire.GetProperty("isProcessing").GetBoolean());
        Assert.Equal(activeScan.Id, wire.GetProperty("operationId").GetGuid());
        Assert.Equal("signalr.cacheSizeScan.scanning", wire.GetProperty("stageKey").GetString());
    }

    [Fact]
    public void CacheSizeScanStatus_Idle_ReportsNothingRunning()
    {
        var body = InvokeCacheSizeScanStatus(BuildCacheController([]));
        var wire = JsonSerializer.SerializeToElement(body, WireOptions);
        Assert.False(wire.GetProperty("isProcessing").GetBoolean());
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("operationId").ValueKind);
    }

    private static object InvokeCacheSizeScanStatus(CacheController controller)
    {
        var result = controller.GetCacheSizeScanStatus();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        return ok.Value!;
    }

    /// <summary>
    /// Builds a <see cref="CacheController"/> and its <see cref="CacheManagementService"/> without
    /// running their real constructors (both take many collaborators the status endpoint never
    /// touches). Only what <c>GetCacheSizeScanStatus</c> reads is wired: the operation tracker and
    /// the cache service's progress context.
    /// </summary>
    private static CacheController BuildCacheController(OperationInfo[] activeScans)
    {
        var cacheService = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(typeof(CacheManagementService));
        SetPrivateField(cacheService, "_currentCacheSizeScanProgressContext", null);

        var tracker = (StubOperationTracker)DispatchProxy.Create<IUnifiedOperationTracker, StubOperationTracker>();
        tracker.ActiveOperations = activeScans;

        var controller = (CacheController)RuntimeHelpers.GetUninitializedObject(typeof(CacheController));
        SetPrivateField(controller, "_cacheService", cacheService);
        SetPrivateField(controller, "_operationTracker", (IUnifiedOperationTracker)(object)tracker);
        return controller;
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    /// <summary>
    /// Minimal <see cref="IUnifiedOperationTracker"/> stub: <c>GetActiveOperations</c> returns the
    /// configured rows; every other member returns its type default. Not sealed for DispatchProxy.Create.
    /// </summary>
    private class StubOperationTracker : DispatchProxy
    {
        public OperationInfo[] ActiveOperations { get; set; } = Array.Empty<OperationInfo>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IUnifiedOperationTracker.GetActiveOperations))
            {
                return ActiveOperations;
            }

            var returnType = targetMethod?.ReturnType;
            if (returnType is null || returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
            {
                return Activator.CreateInstance(returnType);
            }

            return null;
        }
    }
}
