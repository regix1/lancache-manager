using LancacheManager.Middleware;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// A user force stops a full depot crawl while it downloads the Steam Web API app list, a step that does not
/// see the stop, so its code is still running after its card ended. A depot run asked for in that time must
/// not be dropped: a scan asked for through the queue (the depot scan button) waits on a card and starts once
/// the stopped crawl's code has ended, and an import called directly gets the conflict answer and runs when
/// asked again after that.
/// </summary>
public sealed class DepotRunWaitsForStoppedCrawlTests
{
    private const string QueuedScan = "full scan asked for through the queue";
    private const string DirectLocalImport = "local-file import called directly";
    private const string DirectGitHubImport = "GitHub import called directly";

    [Theory]
    [InlineData(QueuedScan)]
    [InlineData(DirectLocalImport)]
    [InlineData(DirectGitHubImport)]
    public async Task ADepotRunAskedForWhileAForceStoppedCrawlIsStillEnding_WaitsOrIsRefusedWithAConflict(string asked)
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        var timeout = ForceStoppedCrawlHost.Timeout;
        host.WriteDepotFile();
        host.SteamApi.PublishDepotFile();

        var stoppedCrawl = await host.StartFullCrawlAsync()
            ?? throw new InvalidOperationException("The full crawl did not start");
        await host.SteamApi.Download(1).Entered.Task.WaitAsync(timeout);
        await host.ForceStopAsync(host.RunningCrawlId);

        if (asked == QueuedScan)
        {
            var outcome = await host.AskForFullCrawlAsync();
            Assert.True(outcome == ForceStoppedCrawlHost.CrawlWaits,
                $"While the force-stopped crawl was still ending, a full scan asked for through the queue was {outcome}; " +
                $"it must be {ForceStoppedCrawlHost.CrawlWaits} and start once the stopped crawl has ended");

            host.SteamApi.Download(1).Fail();
            await stoppedCrawl.WaitAsync(timeout);
            var waited = await host.CrawlFromTheWaitingCardAsync(2);
            Assert.False(waited.IsCompleted, "The scan from the waiting card ended before it listed apps");
            host.SteamApi.Download(2).Fail();
            await waited.WaitAsync(timeout);
            return;
        }

        Task Import() => asked == DirectGitHubImport
            ? host.Service.ImportFromGitHubAsync(notice: new RunNotice(NotificationMode.All, RunTrigger.Manual))
            : host.Service.ImportFromLocalFileAsync(
                CancellationToken.None, new RunNotice(NotificationMode.All, RunTrigger.Manual),
                new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously));

        Exception? refusal = null;
        try
        {
            await Import().WaitAsync(timeout);
        }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            refusal = exception;
        }

        Assert.True(refusal is ConflictException { StageKey: "errors.depot.mappingInProgress" },
            $"While the force-stopped crawl was still ending, the {asked} answered " +
            (refusal is null ? "by running" : $"{refusal.GetType().Name}: {refusal.Message}") +
            "; it must get the conflict answer (errors.depot.mappingInProgress)");

        host.SteamApi.Download(1).Fail();
        await stoppedCrawl.WaitAsync(timeout);
        await Import().WaitAsync(timeout);
        var found = host.Service.GetProgress().DepotMappingsFound;
        var table = await host.TableDepotCountAsync();
        Assert.True(found == table && table > ForceStoppedCrawlHost.TableMappings,
            $"Asked again after the stopped crawl ended, the {asked} left the progress answer counting {found} depot " +
            $"mappings while the table holds {table} (it held {ForceStoppedCrawlHost.TableMappings} before)");
    }
}
