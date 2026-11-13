using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The stand-in schedule loop the run queue tests drive answers the scan type of a held run the way the
/// Game Detection schedule does, so a test of a held run checks what the product does.
/// </summary>
public sealed class ScheduleLoopFakeScanTypeTests
{
    [Fact]
    public void TheRunQueueTestsLoop_AnswersAHeldRunsScanTypeAsGameDetectionDoes()
    {
        // The schedule's own mode is Incremental now; the run was held when its card named the Full scan.
        var state = CacheScanGateHarness.CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetGameDetectionScanMode) => GameDetectionScanMode.Incremental,
            _ => method.ReturnType.IsValueType && Nullable.GetUnderlyingType(method.ReturnType) is null
                ? Activator.CreateInstance(method.ReturnType)
                : null
        });
        var held = new RunNotice(NotificationMode.All, RunTrigger.Manual) { HeldScanType = DetectionScanType.Full };

        using var detection = new GameDetectionService(
            detectionService: null!,
            stateService: state,
            pathResolver: null!,
            scopeFactory: null!,
            cacheReconciliationService: null!,
            operationQueue: null!,
            logger: NullLogger<GameDetectionService>.Instance,
            configuration: new ConfigurationBuilder().Build());
        var productAnswer = detection.ScanTypeOf(held);

        var probeType = typeof(ScheduleRunQueueTests).GetNestedType("LoopProbe", BindingFlags.NonPublic)!;
        using var probe = (ScheduledServiceBase)Activator.CreateInstance(probeType, "gameDetection", false, true, null)!;
        probeType.GetProperty("OwnScanType")!.SetValue(probe, DetectionScanType.Incremental);
        var probeAnswer = probe.ScanTypeOf(held);

        Assert.True(probeAnswer == productAnswer,
            $"Game Detection runs a held run as {productAnswer}, but the run queue tests' loop answers {probeAnswer}");
    }
}
