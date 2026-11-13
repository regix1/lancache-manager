using LancacheManager.Middleware;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// A user force stops a full depot crawl and then asks for a depot scan through the queue (the depot scan
/// button). The stopped crawl's code keeps running after its card ended and reads the stored mappings back
/// while it ends. A scan asked for before that code has ended waits on a card and starts once it has; a
/// scan asked for after it starts at once. Once every run has ended the maps hold the table's rows. Every
/// order runs: the scan is asked for before the stopped crawl wakes, while its read of the table is held,
/// or after it has ended.
/// </summary>
public sealed class ForceStoppedCrawlReloadStartTests
{
    private const string BeforeTheStoppedCrawlWakes = "scan asked before the stopped crawl wakes";
    private const string DuringTheStoppedCrawlsRead = "scan asked while the stopped crawl reads the table";
    private const string AfterTheStoppedCrawlEnded = "scan asked after the stopped crawl ended";

    [Theory]
    [InlineData(BeforeTheStoppedCrawlWakes)]
    [InlineData(DuringTheStoppedCrawlsRead)]
    [InlineData(AfterTheStoppedCrawlEnded)]
    public async Task ADepotScanAskedForAfterAForceStop_RunsOnceTheStoppedCrawlHasEnded(string order)
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        var timeout = ForceStoppedCrawlHost.Timeout;

        var stoppedCrawl = await host.StartFullCrawlAsync()
            ?? throw new InvalidOperationException("The first full crawl did not start");
        await host.SteamApi.Download(1).Entered.Task.WaitAsync(timeout);
        await host.ForceStopAsync(host.RunningCrawlId);

        Task? scan = null;
        if (order == BeforeTheStoppedCrawlWakes)
        {
            var outcome = await host.AskForFullCrawlAsync();
            Assert.True(outcome == ForceStoppedCrawlHost.CrawlWaits,
                $"A depot scan asked for after the force stop, before the stopped crawl woke, was {outcome}; it must " +
                $"be {ForceStoppedCrawlHost.CrawlWaits} until the stopped crawl has ended");
            host.SteamApi.Download(1).Fail();
            await stoppedCrawl.WaitAsync(timeout);
            scan = await host.CrawlFromTheWaitingCardAsync(2);
        }
        else if (order == DuringTheStoppedCrawlsRead)
        {
            host.ReadGate.Arm();
            host.SteamApi.Download(1).Fail();
            // A stopped crawl that does not read the table back ends without reaching the gate.
            await Task.WhenAny(host.ReadGate.Entered.Task, stoppedCrawl).WaitAsync(timeout);
            Assert.True(host.ReadGate.Entered.Task.IsCompleted,
                "The force-stopped crawl ended without reading the stored mappings back into the maps it had emptied");
            var outcome = await host.AskForFullCrawlAsync();
            Assert.True(outcome == ForceStoppedCrawlHost.CrawlWaits,
                "A depot scan asked for after the force stop, while the stopped crawl read the stored mappings back, " +
                $"was {outcome}; it must be {ForceStoppedCrawlHost.CrawlWaits} until the stopped crawl has ended");
            host.ReadGate.Open();
            await stoppedCrawl.WaitAsync(timeout);
            scan = await host.CrawlFromTheWaitingCardAsync(2);
        }
        else
        {
            host.SteamApi.Download(1).Fail();
            await stoppedCrawl.WaitAsync(timeout);
            await host.NoCrawlShown.WaitAsync(timeout);
            // The stopped crawl signs out of Steam when it ends.
            CacheScanGateHarness.LogOnAnonymously(host.Service);
            scan = await host.StartFullCrawlAsync();
            Assert.True(scan is not null, "A depot scan asked for after the force-stopped crawl ended did not start");
            await host.SteamApi.Download(2).Entered.Task.WaitAsync(timeout);
        }

        host.SteamApi.Download(2).Fail();
        await scan!.WaitAsync(timeout);
        var progress = host.Service.GetProgress();
        Assert.True(progress.DepotMappingsFound == ForceStoppedCrawlHost.TableMappings,
            $"Once the stopped crawl and the scan had ended, the progress answer counts {progress.DepotMappingsFound} " +
            $"depot mappings; the table holds {ForceStoppedCrawlHost.TableMappings}");
    }

    private const string FailedGitHubImport = "GitHub import that fails at its download";
    private const string LocalFileImport = "local-file import";
    private const string LaterRunBeforeTheStoppedCrawlWakes = "later run before the stopped crawl wakes";
    private const string LaterRunDuringTheStoppedCrawlsRead = "later run while the stopped crawl reads the table";
    private const string LaterRunAfterTheStoppedCrawlEnded = "later run after the stopped crawl ended";

    /// <summary>
    /// After the force stop, a later depot run that does not itself fill the maps from the table (a GitHub
    /// import whose download fails, a local-file import) is called directly. Called before the stopped
    /// crawl's code has ended it gets the conflict answer, and called again after that it runs to its end.
    /// Once every run has ended the maps hold the table's rows, in every order of the later run against the
    /// stopped crawl's cleanup.
    /// </summary>
    [Theory]
    [InlineData(FailedGitHubImport, LaterRunBeforeTheStoppedCrawlWakes)]
    [InlineData(FailedGitHubImport, LaterRunDuringTheStoppedCrawlsRead)]
    [InlineData(FailedGitHubImport, LaterRunAfterTheStoppedCrawlEnded)]
    [InlineData(LocalFileImport, LaterRunBeforeTheStoppedCrawlWakes)]
    [InlineData(LocalFileImport, LaterRunDuringTheStoppedCrawlsRead)]
    [InlineData(LocalFileImport, LaterRunAfterTheStoppedCrawlEnded)]
    public async Task ALaterRunThatDoesNotFillTheMapsAfterAForceStop_LeavesThemEqualToTheTable(string laterRun, string order)
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        var timeout = ForceStoppedCrawlHost.Timeout;
        if (laterRun == LocalFileImport) host.WriteDepotFile();

        async Task RunTheLaterRunAsync()
        {
            if (laterRun == FailedGitHubImport)
            {
                // The host answers every request but the Steam app list with HTTP 503.
                Assert.False(await host.Service.ImportFromGitHubAsync().WaitAsync(timeout),
                    "The GitHub import did not fail at its download");
            }
            else
            {
                await host.Service.ImportFromLocalFileAsync(
                    CancellationToken.None, new RunNotice(NotificationMode.All, RunTrigger.Manual),
                    new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously)).WaitAsync(timeout);
            }
        }

        async Task CallTheLaterRunBeforeTheStoppedCrawlEndsAsync(string when)
        {
            Exception? refusal = null;
            try
            {
                await RunTheLaterRunAsync();
            }
            catch (Exception exception) when (exception is not TimeoutException)
            {
                refusal = exception;
            }

            Assert.True(refusal is ConflictException { StageKey: "errors.depot.mappingInProgress" },
                $"The {laterRun}, called {when}, answered " +
                (refusal is null ? "by running" : $"{refusal.GetType().Name}: {refusal.Message}") +
                "; until the stopped crawl's code has ended it must get the conflict answer (errors.depot.mappingInProgress)");
        }

        var stoppedCrawl = await host.StartFullCrawlAsync()
            ?? throw new InvalidOperationException("The full crawl did not start");
        await host.SteamApi.Download(1).Entered.Task.WaitAsync(timeout);
        await host.ForceStopAsync(host.RunningCrawlId);

        if (order == LaterRunBeforeTheStoppedCrawlWakes)
        {
            await CallTheLaterRunBeforeTheStoppedCrawlEndsAsync("after the force stop, before the stopped crawl woke");
            host.SteamApi.Download(1).Fail();
            await stoppedCrawl.WaitAsync(timeout);
            await RunTheLaterRunAsync();
        }
        else if (order == LaterRunDuringTheStoppedCrawlsRead)
        {
            host.ReadGate.Arm();
            host.SteamApi.Download(1).Fail();
            // A stopped crawl that does not read the table back ends without reaching the gate.
            await Task.WhenAny(host.ReadGate.Entered.Task, stoppedCrawl).WaitAsync(timeout);
            Assert.True(host.ReadGate.Entered.Task.IsCompleted,
                "The force-stopped crawl ended without reading the stored mappings back into the maps it had emptied");
            await CallTheLaterRunBeforeTheStoppedCrawlEndsAsync("while the stopped crawl read the stored mappings back");
            host.ReadGate.Open();
            await stoppedCrawl.WaitAsync(timeout);
            await RunTheLaterRunAsync();
        }
        else
        {
            host.SteamApi.Download(1).Fail();
            await stoppedCrawl.WaitAsync(timeout);
            await host.NoCrawlShown.WaitAsync(timeout);
            await RunTheLaterRunAsync();
        }

        var found = host.Service.GetProgress().DepotMappingsFound;
        var table = await host.TableDepotCountAsync();
        Assert.True(found == table,
            $"After a force stop and a {laterRun} ({order}), once every run had ended the progress answer counts " +
            $"{found} depot mappings; the table holds {table}");
    }
}
