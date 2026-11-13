using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The host stops while a Run All detection run waits for the operation queue to take it. A shutdown is
/// not a failed scan, so the log holds no error for it.
/// </summary>
[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class DetectionShutdownDuringEnqueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AShutdownWhileARunAllDetectionIsQueued_LogsNoError()
    {
        var logger = new CapturingLogger<GameDetectionService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<OperationStartResponse> EnqueueAsync(object?[] args)
        {
            entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, (CancellationToken)args[4]!);
            return new OperationStartResponse { OperationId = Guid.NewGuid(), Status = OperationStartStatus.Started };
        }

        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);
        using var loop = new GameDetectionService(
            detectionService: null!,
            stateService: DispatchProxy.Create<IStateService, NullReturningProxy>(),
            pathResolver: null!,
            scopeFactory: null!,
            cacheReconciliationService: null!,
            operationQueue: CacheScanGateHarness.CreateProxy<IOperationWaitlist>((_, args) => EnqueueAsync(args!)),
            logger: logger,
            configuration: new ConfigurationBuilder().Build());

        var previousGate = ScheduledServiceBase.ScheduleRunGate;
        var previousWait = ScheduledServiceBase.WaitForDownloadAnswer;
        try
        {
            _ = new ServiceScheduleRegistry(
                [loop],
                CacheScanGateHarness.VisibleClientsStateService(),
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                ScheduleExecutionTestService.Create(),
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Idle());
            ScheduledServiceBase.WaitForDownloadAnswer = null;
            await loop.StartAsync(CancellationToken.None);
            try
            {
                await loop.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.All, RunTrigger.RunAll));
                await entered.Task.WaitAsync(Timeout);
            }
            finally
            {
                await loop.StopAsync(CancellationToken.None);
            }

            var errors = logger.Entries.Where(entry => entry.Level >= LogLevel.Error).ToList();
            Assert.True(errors.Count == 0,
                "The shutdown was logged as an error: " + string.Join(" | ", errors.Select(entry => entry.Message)));
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previousGate;
            ScheduledServiceBase.WaitForDownloadAnswer = previousWait;
        }
    }
}
