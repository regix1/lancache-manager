using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.HistoryImportSource;

namespace LancacheManager.Tests;

/// <summary>
/// A user stops a history import while its first rows are still landing: the stop ends the card at once,
/// before the import has sent any count, and the batch then commits anyway. Whichever side of that commit the
/// stop lands on, and whether the user clicked the card's X before its force stop or not, the download history
/// upgrade must still merge the passes the import wrote, as it does when the stop lands after a count was sent.
/// </summary>
public sealed class ImportStoppedDuringItsFirstRowsReplanTests
{
    private const string WhileFirstBatchWrites = "while its first batch writes";
    private const string AfterFirstBatchCommits = "after its first batch commits";
    private const string WhileSecondBatchWrites = "while its second batch writes";
    private const string ForceStop = "force stop";
    private const string CancelThenForceStop = "cancel, then force stop";

    private static readonly DateTime SessionStart = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(WhileFirstBatchWrites, ForceStop)]
    [InlineData(WhileFirstBatchWrites, CancelThenForceStop)]
    [InlineData(AfterFirstBatchCommits, ForceStop)]
    [InlineData(AfterFirstBatchCommits, CancelThenForceStop)]
    [InlineData(WhileSecondBatchWrites, ForceStop)]
    [InlineData(WhileSecondBatchWrites, CancelThenForceStop)]
    public async Task AnImportStoppedWhileItsFirstRowsLand_HasTheHistoryUpgradeMergeThem(string moment, string stop)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var context = database.Factory.CreateDbContext())
        {
            // Migrated databases keep this default, which the generated test schema omits.
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Downloads\" ALTER COLUMN \"IsEvicted\" SET DEFAULT FALSE");
        }

        var connectionString = database.ConnectionString;
        var contexts = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, settings => settings.EnableRetryOnFailure(3, TimeSpan.Zero, null))
            .Options);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance, notifications);
        var checker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationWaitlistService(tracker, checker, NullLogger<OperationWaitlistService>.Instance);
        var operationState = new OperationStateService(
            NullLogger<OperationStateService>.Instance,
            new ConfigurationBuilder().Build(),
            stateService: null!,
            scopes: null!,
            applicationLifetime: null!,
            processes,
            tracker);
        using var upgrade = new DownloadHistoryUpgradeService(
            contexts, notifications, tracker, queue, operationState, Task.CompletedTask,
            NullLogger<DownloadHistoryUpgradeService>.Instance, checker);

        // The first upgrade already ran on the empty table, so only an import that wrote rows asks for another.
        var first = tracker.RegisterOperation(
            OperationType.DownloadHistoryUpgrade, DownloadHistoryUpgradeService.OperationName, new CancellationTokenSource());
        await upgrade.RunAsync(first, DownloadHistoryUpgradeSql.BatchSize, replan: false, CancellationToken.None);
        tracker.CompleteOperation(first, success: true);
        await upgrade.StartAsync(CancellationToken.None);
        await upgrade.StartupRequest.WaitAsync(ForceStoppedOwnerHost.Timeout);
        var startupRun = upgrade.LastRun;
        if (startupRun is not null) await startupRun.WaitAsync(ForceStoppedOwnerHost.Timeout);

        // The first batch holds two passes of one client on one depot, two seconds apart, which the upgrade merges
        // into one download; the second batch holds one pass of another client an hour later.
        var source = await CreateSourceAsync(connectionString,
        [
            ("10.0.0.9", SessionStart, SessionStart.AddSeconds(1)),
            ("10.0.0.9", SessionStart.AddSeconds(2), SessionStart.AddSeconds(3)),
            ("10.0.0.10", SessionStart.AddHours(1), SessionStart.AddHours(1).AddSeconds(1))
        ]);
        await using var writeGate = new DownloadsWriteGate(connectionString);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var batches = 0;
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var controller = new DataMigrationController(
                NullLogger<DataMigrationController>.Instance,
                notifications,
                tracker,
                queue,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connectionString })
                    .Build(),
                checker,
                upgrade)
            {
                BatchCommitted = async () =>
                {
                    if (Interlocked.Increment(ref batches) != 1) return;
                    if (moment == AfterFirstBatchCommits)
                    {
                        held.TrySetResult();
                        await release.Task;
                    }
                    else if (moment == WhileSecondBatchWrites)
                    {
                        // Taken before the first batch's count is sent, so the second batch stops on its first row.
                        await writeGate.CloseAsync();
                    }
                }
            };

            if (moment == WhileFirstBatchWrites) await writeGate.CloseAsync();
            var import = controller.ImportLancacheManagerAsync(
                new DataMigrationImportRequest { ConnectionString = source, BatchSize = 2 },
                CancellationToken.None);
            if (moment == AfterFirstBatchCommits)
                await held.Task.WaitAsync(ForceStoppedOwnerHost.Timeout);
            else
                await writeGate.WaitForBlockedWriteAsync(ForceStoppedOwnerHost.Timeout);
            var importId = Assert.Single(tracker.GetActiveOperations(OperationType.DataImport)).Id;

            if (stop == CancelThenForceStop)
            {
                // The card's X: the import checks its token only between batches, so the card then offers force stop.
                tracker.CancelOperation(importId);
            }

            // What the card's force stop does to a job without a repair record.
            Assert.True(tracker.ForceKillOperation(importId, followHandoff: false), "The force stop was refused");
            tracker.CompleteOperation(importId, success: false, canceled: true);

            release.TrySetResult();
            await writeGate.OpenAsync();
            await import.WaitAsync(ForceStoppedOwnerHost.Timeout);
        }
        finally
        {
            release.TrySetResult();
            await writeGate.OpenAsync();
            await DropSourceAsync(source);
        }

        Task? replan = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && replan is null)
        {
            replan = upgrade.LastRun is { } run && run != startupRun ? run : null;
            if (replan is null) await Task.Delay(10);
        }

        if (replan is not null) await replan.WaitAsync(ForceStoppedOwnerHost.Timeout);
        await using var check = contexts.CreateDbContext();
        var rows = await check.Downloads.Select(download => download.ClientIp).ToListAsync();
        var duplicated = rows.GroupBy(client => client).Where(group => group.Count() > 1)
            .Select(group => $"{group.Count()} rows for {group.Key}").ToList();
        Assert.True(replan is not null && duplicated.Count == 0,
            $"After a {stop} {moment}, the import left {rows.Count} download rows ({string.Join(", ", duplicated)}) and " +
            (replan is null ? "the download history upgrade never ran, so the passes stay unmerged until a restart" : "the upgrade ran") +
            "; the upgrade must merge the imported passes into one download per client");
    }
}
