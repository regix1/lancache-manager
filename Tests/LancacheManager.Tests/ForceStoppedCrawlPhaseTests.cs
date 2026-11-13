using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// A user force stops a full depot crawl and asks for another. The stopped crawl wakes later and ends as
/// canceled; the phase a poll of the depot progress reads is the newer crawl's. Every order runs: the newer
/// crawl is asked for after the stopped crawl ended, or before it woke (it then waits on a card and starts
/// once the stopped crawl has ended), and is polled while it lists apps or after it ended.
/// </summary>
public sealed class ForceStoppedCrawlPhaseTests
{
    private const string WakesBeforeTheNewerCrawl = "stopped crawl ends before the newer crawl is asked for";
    private const string WakesDuringTheNewerCrawl = "newer crawl asked for before the stopped crawl wakes, polled while it lists apps";
    private const string WakesAfterTheNewerCrawl = "newer crawl asked for before the stopped crawl wakes, polled after it ended";

    [Theory]
    [InlineData(WakesBeforeTheNewerCrawl)]
    [InlineData(WakesDuringTheNewerCrawl)]
    [InlineData(WakesAfterTheNewerCrawl)]
    public async Task AForceStoppedCrawlThatWakesLate_LeavesThePolledPhaseToTheNewerCrawl(string order)
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        var timeout = ForceStoppedCrawlHost.Timeout;

        var stoppedCrawl = await host.StartFullCrawlAsync()
            ?? throw new InvalidOperationException("The first full crawl did not start");
        await host.SteamApi.Download(1).Entered.Task.WaitAsync(timeout);
        await host.ForceStopAsync(host.RunningCrawlId);

        Task newerCrawl;
        if (order == WakesBeforeTheNewerCrawl)
        {
            // The held step sees the force stop's cancel.
            host.SteamApi.Download(1).Answer.TrySetCanceled();
            await stoppedCrawl.WaitAsync(timeout);
            await host.NoCrawlShown.WaitAsync(timeout);
            // The stopped crawl signs out of Steam when it ends.
            CacheScanGateHarness.LogOnAnonymously(host.Service);
            newerCrawl = await host.StartFullCrawlAsync()
                ?? throw new InvalidOperationException("The newer full crawl did not start after the force stop");
            await host.SteamApi.Download(2).Entered.Task.WaitAsync(timeout);
        }
        else
        {
            var outcome = await host.AskForFullCrawlAsync();
            Assert.True(outcome == ForceStoppedCrawlHost.CrawlWaits,
                $"A newer full crawl asked for before the force-stopped crawl woke was {outcome}; it must be " +
                $"{ForceStoppedCrawlHost.CrawlWaits} until the stopped crawl has ended");
            host.SteamApi.Download(1).Answer.TrySetCanceled();
            await stoppedCrawl.WaitAsync(timeout);
            newerCrawl = await host.CrawlFromTheWaitingCardAsync(2);
        }

        var expected = DepotScanPhase.Connecting;
        if (order == WakesAfterTheNewerCrawl)
        {
            // The newer crawl cannot list apps, so it ends in error.
            host.SteamApi.Download(2).Fail();
            await newerCrawl.WaitAsync(timeout);
            expected = DepotScanPhase.Error;
        }

        var polled = host.Service.GetProgress();
        Assert.True(polled.Status == expected,
            $"After the force-stopped crawl woke and ended ({order}), a poll of the depot progress reads the phase " +
            $"{polled.Status} (processing: {polled.IsProcessing}); the newer crawl sets {expected}");
    }
}
