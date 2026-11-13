namespace LancacheManager.Tests;

/// <summary>
/// A full depot crawl is force stopped while it downloads the Steam Web API app list, and a second full
/// crawl is asked for. The stopped crawl reads the stored mappings back when it ends; that read never lands
/// in the second crawl, which emptied its mappings when it started. Both orders run: the second crawl is
/// asked for after the stopped crawl ended, or before (it then waits on a card until the stopped crawl has
/// ended). A third case starts no
/// run until the stopped crawl has ended: the maps then hold the table's rows again, and a crawl started
/// afterward still starts.
/// </summary>
public sealed class ForceStoppedCrawlReloadTests
{
    private const string SecondInTheGap = "second crawl starts before the stopped crawl ends";
    private const string SecondAfter = "second crawl starts after the stopped crawl ends";
    private const string NoRunUntilTheEnd = "no run until the stopped crawl ends";

    [Theory]
    [InlineData(SecondAfter)]
    [InlineData(SecondInTheGap)]
    [InlineData(NoRunUntilTheEnd)]
    public async Task ASecondFullCrawlAfterAForceStop_HoldsNoMappingsTheStoppedCrawlReadBack(string order)
    {
        var secondStartsBeforeTheStoppedCrawlEnds = order == SecondInTheGap;
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        var timeout = ForceStoppedCrawlHost.Timeout;
        var service = host.Service;
        Assert.Equal(3, service.GetProgress().DepotMappingsFound);

        var stoppedCrawl = await host.StartFullCrawlAsync();
        Assert.True(stoppedCrawl is not null, "The first full crawl did not start");
        await host.SteamApi.Download(1).Entered.Task.WaitAsync(timeout);
        await host.ForceStopAsync(host.RunningCrawlId);

        if (secondStartsBeforeTheStoppedCrawlEnds)
        {
            // Asked for while the stopped crawl's code still runs, the second crawl waits on a card and starts
            // once that code has ended.
            var outcome = await host.AskForFullCrawlAsync();
            Assert.True(outcome == ForceStoppedCrawlHost.CrawlWaits,
                $"A second full crawl asked for before the force-stopped crawl ended was {outcome}; it must be " +
                $"{ForceStoppedCrawlHost.CrawlWaits} until the stopped crawl has ended");
            host.SteamApi.Download(1).Fail();
            await stoppedCrawl!.WaitAsync(timeout);
            await host.CrawlFromTheWaitingCardAsync(2);
        }
        else
        {
            host.SteamApi.Download(1).Fail();
            await stoppedCrawl!.WaitAsync(timeout);
            await host.NoCrawlShown.WaitAsync(timeout);
            // The stopped crawl signs out of Steam when it ends.
            CacheScanGateHarness.LogOnAnonymously(service);
            if (order == NoRunUntilTheEnd)
            {
                var afterTheStop = service.GetProgress();
                Assert.True(afterTheStop.DepotMappingsFound == 3,
                    $"After the force-stopped full crawl ended with no later run, the progress answer counts {afterTheStop.DepotMappingsFound} depot mappings; the table holds 3");
            }

            var secondCrawl = await host.StartFullCrawlAsync();
            Assert.True(secondCrawl is not null, "The second full crawl did not start once no crawl was shown running");
            await host.SteamApi.Download(2).Entered.Task.WaitAsync(timeout);
        }

        var progress = service.GetProgress();
        Assert.True(progress.DepotMappingsFound == 0,
            $"While the second full crawl lists apps its progress counts {progress.DepotMappingsFound} depot mappings; " +
            "it emptied them when it started, so they are the rows the force-stopped crawl read back after that");
    }
}
