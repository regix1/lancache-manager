using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// After a GitHub mapping import the change marker is the published file's change number. When the
/// viability check reports that Steam still knows that number (no full scan required), a Hybrid
/// schedule inside its week runs an incremental crawl: it does not skip asking the user to start a
/// full scan by hand, and it does not start a GitHub import by itself, which stays its own action.
/// The check is the CheckChangesAsync seam, answered with requiresFullScan false; how the check
/// reads Steam's RequiresFullUpdate and RequiresFullAppUpdate flags is not covered here.
/// </summary>
public sealed class SteamKit2HybridFullAppUpdateTests
{
    [Fact]
    public async Task AHybridRunThatSteamAnswersWithAFullAppUpdateDoesNotWaitForAManualFullScanAsync()
    {
        // The state a GitHub import leaves (the published file's mappings and change number on disk,
        // the same number committed as the marker, the last full crawl inside the Hybrid week), with
        // the check from that marker answering 9,053 changes later that Steam still knows it.
        // Another job holds the queue, so the work the run asks for waits on the queue as the real
        // waitlist parks it, and nothing reaches Steam or GitHub.
        var queue = new SteamKit2HybridFullScanFallbackTests.ParkingWaitlist();
        await using var harness = await SteamKit2HybridFullScanFallbackTests.Harness.CreateAsync(
            queue, requiresFullScan: false);
        var service = harness.Service;
        var sent = harness.Sent;

        await service.RunScheduledTickAsync(CancellationToken.None);

        var skippedForAFullScan = service.PendingFullScan != null
            || sent.Events.Any(item => item.EventName == SignalREvents.AutomaticScanSkipped);
        Assert.False(
            skippedForAFullScan,
            "the Hybrid run was skipped with \"Steam requires full scan ... User must manually trigger a full scan\"");
        Assert.True(
            queue.DepotMappingRequests.Contains(DepotMappingRequestKey.Crawl(incrementalOnly: true)),
            "the Hybrid run asked for no incremental crawl, so the schedule did nothing this hour");
        Assert.False(
            queue.DepotMappingRequests.Contains(new DepotMappingRequestKey(DepotMappingWork.GitHubImport)),
            "the Hybrid run started a GitHub import by itself, which stays a separate action");
    }
}
