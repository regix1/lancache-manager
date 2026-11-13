using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A user starts a GitHub or local depot import from the depot page, and the page's request ends while the import
/// still runs (a reload, a closed tab, a proxy timeout). Nobody stopped the import, so it must run to its end and its
/// card must read completed, whether the request ends while the import runs or after it answered.
/// </summary>
public sealed class DepotImportRequestEndTests
{
    private const string GitHub = "github";
    private const string Local = "local";
    private const string RequestEndsWhileTheImportRuns = "request ends while the import runs";
    private const string RequestEndsAfterTheImportAnswered = "request ends after the import answered";

    [Theory]
    [InlineData(GitHub, RequestEndsWhileTheImportRuns)]
    [InlineData(GitHub, RequestEndsAfterTheImportAnswered)]
    [InlineData(Local, RequestEndsWhileTheImportRuns)]
    [InlineData(Local, RequestEndsAfterTheImportAnswered)]
    public async Task ADepotImportStartedFromThePage_RunsToItsEndWhenTheRequestEnds(string source, string order)
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        if (source == GitHub) host.SteamApi.PublishDepotFile();
        else host.WriteDepotFile();
        var tracker = (UnifiedOperationTracker)CacheScanGateHarness.GetField(host.Service, "_operationTracker")!;
        var container = (ServiceProvider)CacheScanGateHarness.GetField(host, "_container")!;
        var controller = new DepotsController(
            host.Service,
            (PicsDataService)CacheScanGateHarness.GetField(host.Service, "_picsDataService")!,
            (StateService)CacheScanGateHarness.GetField(host.Service, "_stateService")!,
            NullLogger<DepotsController>.Instance,
            container.GetRequiredService<IOperationConflictChecker>(),
            container.GetRequiredService<IOperationWaitlist>());

        using var request = new CancellationTokenSource();
        if (order == RequestEndsWhileTheImportRuns)
        {
            // The import's read of the mappings table waits until the request has ended.
            host.ReadGate.Arm();
            var answer = controller.ImportDepotMappingsAsync(source, request.Token);
            await Task.WhenAny(host.ReadGate.Entered.Task, answer).WaitAsync(ForceStoppedCrawlHost.Timeout);
            Assert.True(host.ReadGate.Entered.Task.IsCompleted, $"The {source} import ended before it read the mappings table");
            await request.CancelAsync();
            host.ReadGate.Open();
            await Record.ExceptionAsync(() => answer.WaitAsync(ForceStoppedCrawlHost.Timeout));
        }
        else
        {
            await controller.ImportDepotMappingsAsync(source, request.Token).WaitAsync(ForceStoppedCrawlHost.Timeout);
            await request.CancelAsync();
        }

        var run = Assert.Single(tracker.GetRuns().Runs, candidate => candidate.OperationType == "depotMapping");
        Assert.True(run.Status == "completed",
            $"The {source} import started from the page, whose {order}, ended {run.Status} ({run.Message}); nobody " +
            "stopped it, so it must end completed");
    }
}
