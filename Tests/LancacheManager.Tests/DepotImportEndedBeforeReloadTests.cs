using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// A GitHub import or a local-file import has written its depots into the mappings table and is reading the
/// table back into the maps when it ends early: the user cancels it, or the read fails. Once it has ended, the
/// depot progress answer must count the table's depots, the rows the import committed included.
/// </summary>
public sealed class DepotImportEndedBeforeReloadTests
{
    private const string GitHubImport = "GitHub import";
    private const string LocalImport = "local-file import";
    private const string Canceled = "the user cancels it";
    private const string ReadFails = "the read fails";

    [Theory]
    [InlineData(GitHubImport, Canceled)]
    [InlineData(GitHubImport, ReadFails)]
    [InlineData(LocalImport, Canceled)]
    [InlineData(LocalImport, ReadFails)]
    public async Task AnImportThatEndsWhileReadingTheTableBack_LeavesTheProgressAnswerCountingTheTablesDepots(
        string importKind, string ending)
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        var timeout = ForceStoppedCrawlHost.Timeout;
        host.WriteDepotFile();
        host.SteamApi.PublishDepotFile();

        // The first read of the table's rows after the import's write is its reload into the maps.
        host.ReadGate.ArmRowRead();
        Task import = importKind == GitHubImport
            ? host.Service.ImportFromGitHubAsync(notice: new RunNotice(NotificationMode.All, RunTrigger.Manual))
            : host.Service.ImportFromLocalFileAsync(
                CancellationToken.None, new RunNotice(NotificationMode.All, RunTrigger.Manual),
                new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously));
        await Task.WhenAny(host.ReadGate.Entered.Task, import).WaitAsync(timeout);

        var reachedTheReload = host.ReadGate.Entered.Task.IsCompleted;
        if (reachedTheReload)
        {
            if (ending == Canceled)
            {
                Assert.True(await host.Service.CancelRebuildAsync(), $"The {importKind}'s cancel was refused");
                host.ReadGate.Open();
            }
            else
            {
                host.ReadGate.FailTheRead();
            }
        }

        try
        {
            await import.WaitAsync(timeout);
        }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            // The import reports its own cancel or failure; the maps are what this test reads.
        }

        var found = host.Service.GetProgress().DepotMappingsFound;
        var table = await host.TableDepotCountAsync();
        Assert.True(found == table,
            $"The {importKind} wrote its depots into the table and {(reachedTheReload ? ending + " while it read the table back into the maps" : "ended without reading the table back into the maps")}; " +
            $"once it ended, the progress answer counts {found} depot mappings while the table holds {table} " +
            $"(it held {ForceStoppedCrawlHost.TableMappings} before the import)");
    }
}
