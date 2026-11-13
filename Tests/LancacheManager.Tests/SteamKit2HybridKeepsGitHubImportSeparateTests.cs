using System.Text.Json;
using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// The GitHub mapping import is its own action and its own mode. A Hybrid schedule that Steam asks for a
/// full scan, while the Steam Web API cannot list apps, skips the run and asks the user for a full scan;
/// it never starts a GitHub import by itself.
/// </summary>
public sealed class SteamKit2HybridKeepsGitHubImportSeparateTests
{
    private const string FullScanRequiredSkipStageKey = "signalr.depotMapping.scan.skippedFullRequired";

    [Fact]
    public async Task AHybridRunThatNeedsAFullScanWithoutTheWebApiSkipsAndAsksInsteadOfImportingFromGitHubAsync()
    {
        // Steam answers the check with RequiresFullUpdate, no Web API key is stored, and another job holds
        // the queue, so any depot mapping work the tick asks for lands on the waitlist where it is seen.
        var queue = new SteamKit2HybridFullScanFallbackTests.ParkingWaitlist();
        await using var harness = await SteamKit2HybridFullScanFallbackTests.Harness.CreateAsync(queue);

        await harness.Service.RunScheduledTickAsync(CancellationToken.None);

        var askedForGitHubImport = queue.DepotMappingRequests
            .Contains(new DepotMappingRequestKey(DepotMappingWork.GitHubImport));
        var leftFullScanPrompt = harness.Service.PendingFullScan is not null;
        var sentSkippedEvent = harness.Sent.Events
            .Any(item => item.EventName == SignalREvents.AutomaticScanSkipped);
        var endedWithFullScanSkip = harness.Sent.Events
            .Any(item => item.EventName == "DepotMappingComplete"
                && JsonSerializer.Serialize(item.Payload, item.Payload!.GetType())
                    .Contains(FullScanRequiredSkipStageKey, StringComparison.Ordinal));

        Assert.False(askedForGitHubImport, "the Hybrid run asked the operation queue for a GitHub mapping import by itself");
        Assert.True(leftFullScanPrompt, "the Hybrid run left no full-scan prompt for the schedules list to show");
        Assert.True(sentSkippedEvent, "the Hybrid run sent no AutomaticScanSkipped event asking the user for a full scan");
        Assert.True(endedWithFullScanSkip, "the Hybrid run did not end its card with the full-scan-required skip");
    }
}
