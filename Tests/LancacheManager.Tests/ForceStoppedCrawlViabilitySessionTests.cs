using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// A full depot crawl ends, and no newer crawl starts. A viability check whose Steam request fails then
/// drops the Steam session, as it does whenever no crawl or sign-in owns it. Every case runs: the user
/// force stops the crawl and the check comes while the stopped crawl is still held in its app-list
/// download (the crawl's code still owns the session then, so the check leaves it and the crawl signs out
/// when it ends), or after it ended; or the crawl ends on its own, with no force stop, before the check.
/// </summary>
public sealed class ForceStoppedCrawlViabilitySessionTests
{
    private const string CheckBeforeTheStoppedCrawlEnds = "check before the stopped crawl ends";
    private const string CheckAfterTheStoppedCrawlEnded = "check after the stopped crawl ended";
    private const string CheckAfterTheCrawlEndedOnItsOwn = "no force stop, check after the crawl ended on its own";

    [Theory]
    [InlineData(CheckBeforeTheStoppedCrawlEnds)]
    [InlineData(CheckAfterTheStoppedCrawlEnded)]
    [InlineData(CheckAfterTheCrawlEndedOnItsOwn)]
    public async Task AFailedViabilityCheckAfterAForceStop_DropsTheSteamSession(string order)
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        var timeout = ForceStoppedCrawlHost.Timeout;
        // The saved depot file and the table give the check a baseline, so it asks Steam.
        host.WriteDepotFile();

        var stoppedCrawl = await host.StartFullCrawlAsync()
            ?? throw new InvalidOperationException("The full crawl did not start");
        await host.SteamApi.Download(1).Entered.Task.WaitAsync(timeout);
        if (order == CheckAfterTheCrawlEndedOnItsOwn)
        {
            // Steam's app list is unavailable, so the crawl ends on that error.
            host.SteamApi.Download(1).Fail();
            await stoppedCrawl.WaitAsync(timeout);
            Assert.False(host.Service.IsRebuildRunning, "The crawl still showed as running after it ended");
            // The crawl signs out of Steam when it ends.
            CacheScanGateHarness.LogOnAnonymously(host.Service);
        }
        else
        {
            await host.ForceStopAsync(host.RunningCrawlId);
        }

        if (order == CheckAfterTheStoppedCrawlEnded)
        {
            host.SteamApi.Download(1).Fail();
            await stoppedCrawl.WaitAsync(timeout);
            // The stopped crawl signs out of Steam when it ends.
            CacheScanGateHarness.LogOnAnonymously(host.Service);
        }

        // With no callback manager the check's PICS request fails at once, as a request Steam does not answer
        // fails after its limit.
        var callbacks = CacheScanGateHarness.GetField(host.Service, "_manager");
        CacheScanGateHarness.SetField(host.Service, "_manager", null);
        IncrementalViabilityCheck result;
        try
        {
            result = await host.Service.CheckViabilityAsync(CancellationToken.None).WaitAsync(timeout);
        }
        finally
        {
            CacheScanGateHarness.SetField(host.Service, "_manager", callbacks);
        }

        Assert.True(result.StageKey == "errors.steam.connectionFailed",
            $"The viability check did not fail on its Steam request (stage {result.StageKey}, error {result.Error})");
        var signedOn = (bool)CacheScanGateHarness.GetField(host.Service, "_isLoggedOn")!;
        if (order == CheckBeforeTheStoppedCrawlEnds)
        {
            // The stopped crawl's code still runs and owns the session until it ends, so the failed check leaves
            // it; the crawl signs out of Steam itself when it ends.
            Assert.True(signedOn,
                $"A viability check whose Steam request failed ({result.Error}) while the force-stopped crawl was still " +
                "held in its app-list download dropped the Steam session that crawl still owns");
            var logOffsBefore = host.LogOffs.Count;
            host.SteamApi.Download(1).Fail();
            await stoppedCrawl.WaitAsync(timeout);
            Assert.True(host.LogOffs.Count > logOffsBefore,
                "Once the force-stopped crawl ended after the failed viability check, no log-off was sent to Steam; the " +
                "crawl that owned the session leaves it signed on with no run using it");
            return;
        }

        Assert.False(signedOn,
            $"After the full crawl ({order}), a viability check whose Steam request failed ({result.Error}) " +
            "left the Steam session signed on; no crawl was running, so the failed check drops it");
    }
}
