namespace LancacheManager.Tests;

/// <summary>
/// A user force stops a full depot crawl and asks for another, which signs on with the Steam session that
/// is open. No log-off is sent to Steam while the newer crawl uses that session. Both orders run: the newer
/// crawl is asked for before the stopped crawl ends (it then waits on a card and starts once the stopped
/// crawl has ended), or after.
/// </summary>
public sealed class ForceStoppedCrawlSessionTests
{
    private const string NewerStartsBeforeTheStoppedCrawlEnds = "newer crawl starts before the stopped crawl ends";
    private const string NewerStartsAfterTheStoppedCrawlEnded = "newer crawl starts after the stopped crawl ended";

    [Theory]
    [InlineData(NewerStartsBeforeTheStoppedCrawlEnds)]
    [InlineData(NewerStartsAfterTheStoppedCrawlEnded)]
    public async Task AForceStoppedCrawlThatEndsLate_LeavesTheNewerCrawlsSteamSessionSignedOn(string order)
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        var timeout = ForceStoppedCrawlHost.Timeout;

        var stoppedCrawl = await host.StartFullCrawlAsync()
            ?? throw new InvalidOperationException("The first full crawl did not start");
        await host.SteamApi.Download(1).Entered.Task.WaitAsync(timeout);
        await host.ForceStopAsync(host.RunningCrawlId);

        Task newerCrawl;
        int logOffsBefore;
        if (order == NewerStartsBeforeTheStoppedCrawlEnds)
        {
            // Asked for while the stopped crawl's code still runs, the newer crawl waits on a card and starts
            // once that code has ended, so the stopped crawl's log-off lands before the newer crawl signs on.
            var outcome = await host.AskForFullCrawlAsync();
            Assert.True(outcome == ForceStoppedCrawlHost.CrawlWaits,
                $"A newer full crawl asked for before the force-stopped crawl ended was {outcome}; it must be " +
                $"{ForceStoppedCrawlHost.CrawlWaits} until the stopped crawl has ended");
            host.SteamApi.Download(1).Fail();
            await stoppedCrawl.WaitAsync(timeout);
            newerCrawl = await host.CrawlFromTheWaitingCardAsync(2);
            logOffsBefore = host.LogOffs.Count;
        }
        else
        {
            host.SteamApi.Download(1).Fail();
            await stoppedCrawl.WaitAsync(timeout);
            await host.NoCrawlShown.WaitAsync(timeout);
            // The stopped crawl signs out of Steam when it ends.
            CacheScanGateHarness.LogOnAnonymously(host.Service);
            newerCrawl = await host.StartFullCrawlAsync()
                ?? throw new InvalidOperationException("The newer full crawl did not start after the force stop");
            await host.SteamApi.Download(2).Entered.Task.WaitAsync(timeout);
            logOffsBefore = host.LogOffs.Count;
        }

        var logOffs = host.LogOffs.Count - logOffsBefore;
        Assert.True(logOffs == 0,
            $"While the newer full crawl was using the Steam session, {logOffs} log-off(s) were sent to Steam as the " +
            "force-stopped crawl ended; Steam ends the session a log-off is sent on, so the newer crawl loses it");
        Assert.False(newerCrawl.IsCompleted, "The newer full crawl ended before the check");
    }
}
