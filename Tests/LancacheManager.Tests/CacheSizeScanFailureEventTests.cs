using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.CacheScanGateHarness;

namespace LancacheManager.Tests;

/// <summary>
/// A failed cache file scan ends on a key whose sentence names a value (the cache folder that went
/// missing, the error text). The completion event must carry that key with its values, because the
/// browser fills the sentence from the event's context only when the error is the stage key; without
/// them the live card reads the raw placeholder.
/// </summary>
public sealed class CacheSizeScanFailureEventTests
{
    private const string WalkerError = "The cache disk stopped answering";

    [Fact]
    public async Task AMissingCacheRoot_SendsTheFolderWithItsKeyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "lm-scan-missing-" + Guid.NewGuid().ToString("N"));

        var complete = await RunFailedScanAsync(root, DispatchProxy.Create<IPathResolver, PathResolverProxy>());

        Assert.Equal("signalr.cacheSizeScan.rootUnavailable", complete.StageKey);
        Assert.Equal(complete.StageKey, complete.Error);
        Assert.NotNull(complete.Context);
        Assert.Equal(root, Assert.IsType<string>(complete.Context!["cachePath"]));
    }

    [Fact]
    public async Task AWalkerFailure_SendsTheErrorTextWithItsKeyAsync()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "lm-scan-fatal-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            // The walker's first step fails the way an unreadable install folder does.
            var complete = await RunFailedScanAsync(root, CreateProxy<IPathResolver>((_, _) =>
                throw new IOException(WalkerError)));

            Assert.Equal("signalr.cacheSizeScan.error.fatal", complete.StageKey);
            Assert.Equal(complete.StageKey, complete.Error);
            Assert.NotNull(complete.Context);
            Assert.Equal(WalkerError, Assert.IsType<string>(complete.Context!["errorDetail"]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AMissingWalker_SendsItsKeyWithNoValuesAsync()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "lm-scan-no-walker-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            // The resolver names a walker binary under the root that was never written.
            var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)pathResolver).Root = root;

            var complete = await RunFailedScanAsync(root, pathResolver);

            Assert.Equal("signalr.cacheSizeScan.failed", complete.Error);
            Assert.Null(complete.Context);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<CacheSizeScanComplete> RunFailedScanAsync(string cachePath, IPathResolver pathResolver)
    {
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, CacheScanDetectionPhaseTests.RecordingNotifications>();
        var recorder = (CacheScanDetectionPhaseTests.RecordingNotifications)(object)notifications;
        var service = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(typeof(CacheManagementService));
        using var scanLock = new SemaphoreSlim(1, 1);
        SetField(service, "_logger", NullLogger<CacheManagementService>.Instance);
        SetField(service, "_scanCacheLock", scanLock);
        SetField(service, "_operationTracker", tracker);
        SetField(service, "_notifications", notifications);
        SetField(service, "_pathResolver", pathResolver);
        SetField(service, "_conflictChecker", OperationConflictTestServices.Create(tracker,
            NullLogger<OperationConflictChecker>.Instance));
        SetField(service, "_operationStateService", OperationConflictTestServices.Owner);
        var run = typeof(CacheManagementService).GetMethod("RunFullScanAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)run.Invoke(service, [new List<string> { cachePath }, CancellationToken.None, null, null])!;

        return Assert.IsType<CacheSizeScanComplete>(
            await recorder.WaitForAsync(SignalREvents.CacheSizeScanComplete, _ => true));
    }
}
