using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Tests;

/// <summary>
/// Pins the recovery-status contract for the three pipelines whose live cards are gated on a
/// run-stable <c>showNotification</c> flag (game detection, cache-file scan, Epic game mapping).
/// Lifecycle events are always emitted so recovery works, but a silent automatic run's terminal is
/// display-gated. If the recovery status endpoint did NOT expose the run's flag, a page reload mid-run
/// would resurrect a visible card that the silent terminal can never clear. These tests lock each
/// status response to carry the active run's flag (camelCase on the wire) so the frontend recovery
/// gate can skip resurrecting a silent run's card.
/// </summary>
public class RecoveryStatusNotificationFlagTests
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    // ---- Game detection: GET /api/games/detect/active -> ActiveDetectionResponse ----

    [Fact]
    public void ActiveDetectionResponse_SilentRun_SerializesHiddenFlagCamelCase()
    {
        var response = new ActiveDetectionResponse
        {
            IsProcessing = true,
            Operation = null,
            ShowNotification = false
        };

        var json = JsonSerializer.Serialize(response, WireOptions);
        Assert.Contains("\"showNotification\":false", json);
    }

    [Fact]
    public void ActiveDetectionResponse_DefaultsToVisible()
    {
        var response = new ActiveDetectionResponse { IsProcessing = false, Operation = null };
        Assert.True(response.ShowNotification);

        var json = JsonSerializer.Serialize(response, WireOptions);
        Assert.Contains("\"showNotification\":true", json);
    }

    // ---- Epic game mapping: GET /api/epic/game-mappings/schedule -> EpicScheduleStatus ----

    [Fact]
    public void EpicScheduleStatus_SilentRun_SerializesHiddenFlagCamelCase()
    {
        var status = new EpicScheduleStatus
        {
            IsProcessing = true,
            ShowNotification = false
        };

        var json = JsonSerializer.Serialize(status, WireOptions);
        Assert.Contains("\"isProcessing\":true", json);
        Assert.Contains("\"showNotification\":false", json);
    }

    [Fact]
    public void EpicScheduleStatus_DefaultsToVisible()
    {
        var status = new EpicScheduleStatus();
        Assert.True(status.ShowNotification);

        var json = JsonSerializer.Serialize(status, WireOptions);
        Assert.Contains("\"showNotification\":true", json);
    }

    // ---- Cache-file scan: GET /api/cache/size/scan/status (anonymous body from the controller) ----

    [Fact]
    public void CacheSizeScanStatus_ActiveSilentRun_ReportsHiddenFlag()
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

        var controller = BuildCacheController(
            showNotification: false,
            activeScans: new[] { activeScan });

        var body = InvokeCacheSizeScanStatus(controller);
        Assert.True(ReadBool(body, "isProcessing"));
        Assert.False(ReadBool(body, "showNotification"));
    }

    [Fact]
    public void CacheSizeScanStatus_Idle_ReportsVisibleSoMissedTerminalsStaleComplete()
    {
        // No active scan: recovery must NOT take the silent-skip branch, so the idle response stays
        // visible and a missed terminal still stale-completes the card.
        var controller = BuildCacheController(showNotification: null, activeScans: Array.Empty<OperationInfo>());

        var body = InvokeCacheSizeScanStatus(controller);
        Assert.False(ReadBool(body, "isProcessing"));
        Assert.True(ReadBool(body, "showNotification"));
    }

    private static object InvokeCacheSizeScanStatus(CacheController controller)
    {
        var result = controller.GetCacheSizeScanStatus();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        return ok.Value!;
    }

    private static bool ReadBool(object body, string propertyName)
    {
        var prop = body.GetType().GetProperty(propertyName);
        Assert.NotNull(prop);
        return (bool)prop!.GetValue(body)!;
    }

    /// <summary>
    /// Builds a <see cref="CacheController"/> and its <see cref="CacheManagementService"/> without
    /// running their real constructors (both take many collaborators the status endpoint never
    /// touches). Only the two fields <c>GetCacheSizeScanStatus</c> reads are wired: the operation
    /// tracker and the cache service's run-stable visibility flag.
    /// </summary>
    private static CacheController BuildCacheController(bool? showNotification, OperationInfo[] activeScans)
    {
        var cacheService = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(typeof(CacheManagementService));
        SetBackingField(cacheService, nameof(CacheManagementService.CurrentCacheSizeScanShowNotification), showNotification);
        SetBackingField(cacheService, nameof(CacheManagementService.CurrentCacheSizeScanProgressContext), null);

        var tracker = (StubOperationTracker)DispatchProxy.Create<IUnifiedOperationTracker, StubOperationTracker>();
        tracker.ActiveOperations = activeScans;

        var controller = (CacheController)RuntimeHelpers.GetUninitializedObject(typeof(CacheController));
        SetPrivateField(controller, "_cacheService", cacheService);
        SetPrivateField(controller, "_operationTracker", (IUnifiedOperationTracker)(object)tracker);
        return controller;
    }

    private static void SetBackingField(object target, string autoPropertyName, object? value)
    {
        var field = target.GetType().GetField(
            $"<{autoPropertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
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
