namespace LancacheManager.Tests;

/// <summary>
/// A full depot crawl that cannot list apps reads the stored mappings back as it ends. A user force stops
/// it while that read is held, and a second full crawl starts. The second crawl emptied its maps when it
/// started, so the stopped crawl's read never lands in them. Every order runs: the second crawl is asked
/// for before the read returns (it waits on a card until the stopped crawl has ended), after it, or not at
/// all (the maps then hold the table's rows).
/// </summary>
public sealed class ForceStopDuringCrawlReloadTests
{
    private const string SecondBeforeTheReadReturns = "second crawl starts before the stopped crawl's read returns";
    private const string SecondAfterTheReadReturns = "second crawl starts after the stopped crawl's read returns";
    private const string NoSecondCrawl = "no second crawl";

    [Theory]
    [InlineData(SecondBeforeTheReadReturns)]
    [InlineData(SecondAfterTheReadReturns)]
    [InlineData(NoSecondCrawl)]
    public async Task AForceStopDuringTheCrawlsOwnReload_KeepsItsReadOutOfTheNextCrawl(string order)
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        var timeout = ForceStoppedCrawlHost.Timeout;

        var stoppedCrawl = await host.StartFullCrawlAsync()
            ?? throw new InvalidOperationException("The first full crawl did not start");
        await host.SteamApi.Download(1).Entered.Task.WaitAsync(timeout);
        var stoppedId = host.RunningCrawlId;
        host.ReadGate.Arm();
        host.SteamApi.Download(1).Fail();
        await Task.WhenAny(host.ReadGate.Entered.Task, stoppedCrawl).WaitAsync(timeout);
        await host.ForceStopAsync(stoppedId);

        if (order == NoSecondCrawl)
        {
            host.ReadGate.Open();
            await stoppedCrawl.WaitAsync(timeout);
            var afterTheStop = host.Service.GetProgress();
            Assert.True(afterTheStop.DepotMappingsFound == ForceStoppedCrawlHost.TableMappings,
                $"After the force-stopped full crawl ended with no later run, the progress answer counts {afterTheStop.DepotMappingsFound} " +
                $"depot mappings; the table holds {ForceStoppedCrawlHost.TableMappings}");
            return;
        }

        if (order == SecondBeforeTheReadReturns)
        {
            // Asked for while the stopped crawl's code still runs, the second crawl waits on a card and starts
            // once that code has ended.
            var outcome = await host.AskForFullCrawlAsync();
            Assert.True(outcome == ForceStoppedCrawlHost.CrawlWaits,
                $"A second full crawl asked for while the force-stopped crawl's read was held was {outcome}; it must be " +
                $"{ForceStoppedCrawlHost.CrawlWaits} until the stopped crawl has ended");
            host.ReadGate.Open();
            await stoppedCrawl.WaitAsync(timeout);
            await host.CrawlFromTheWaitingCardAsync(2);
        }
        else
        {
            host.ReadGate.Open();
            await stoppedCrawl.WaitAsync(timeout);
            await host.NoCrawlShown.WaitAsync(timeout);
            // The stopped crawl signs out of Steam when it ends.
            CacheScanGateHarness.LogOnAnonymously(host.Service);
            var secondCrawl = await host.StartFullCrawlAsync();
            Assert.True(secondCrawl is not null, "The second full crawl did not start after the force stop");
            await host.SteamApi.Download(2).Entered.Task.WaitAsync(timeout);
        }

        var progress = host.Service.GetProgress();
        Assert.True(progress.DepotMappingsFound == 0,
            $"While the second full crawl lists apps its progress counts {progress.DepotMappingsFound} depot mappings; " +
            "it emptied them when it started, so they are the rows the force-stopped crawl's own read returned after that");
    }
}
