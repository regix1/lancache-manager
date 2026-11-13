using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// The local-file depot import (POST /api/depots/import?source=local) adds the depot file on disk to the
/// mappings table. Once it has ended, the depot progress answer counts the table's depots, as it does after
/// a GitHub import.
/// </summary>
public sealed class LocalDepotImportMapsTests
{
    [Fact]
    public async Task ALocalFileImport_LeavesTheProgressAnswerCountingTheTablesDepots()
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        host.WriteDepotFile();

        await host.Service.ImportFromLocalFileAsync(
            CancellationToken.None, new RunNotice(NotificationMode.All, RunTrigger.Manual),
            new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously))
            .WaitAsync(ForceStoppedCrawlHost.Timeout);

        var found = host.Service.GetProgress().DepotMappingsFound;
        var table = await host.TableDepotCountAsync();
        Assert.True(found == table,
            $"After the local-file import ended, the progress answer counts {found} depot mappings; the table holds " +
            $"{table} (it held {ForceStoppedCrawlHost.TableMappings} before the import)");
    }
}
