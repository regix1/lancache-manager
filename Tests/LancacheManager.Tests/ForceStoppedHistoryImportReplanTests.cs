using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.HistoryImportSource;

namespace LancacheManager.Tests;

/// <summary>
/// A user stops a history import after it already wrote rows: the import is held right after its second batch
/// committed, once its first batch's progress was sent. Whether the stop is the card's X or its force stop, the
/// download history upgrade must then merge the imported rows, as it does after any import that wrote rows.
/// </summary>
public sealed class ForceStoppedHistoryImportReplanTests
{
    private const string ForceStop = "force stop";
    private const string Cancel = "cancel";

    private static readonly DateTime SessionStart = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ForceStop)]
    [InlineData(Cancel)]
    public async Task AnImportStoppedAfterItWroteRows_HasTheHistoryUpgradeMergeThem(string stop)
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

        // Two passes of one client on one depot, two seconds apart: the upgrade merges them into one download.
        var source = await CreateSourceAsync(connectionString,
        [
            ("10.0.0.9", SessionStart, SessionStart.AddSeconds(1)),
            ("10.0.0.9", SessionStart.AddSeconds(2), SessionStart.AddSeconds(3))
        ]);
        try
        {
            var batches = 0;
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var importLog = new CapturingLogger<DataMigrationController>();
            var controller = new DataMigrationController(
                importLog,
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
                    if (Interlocked.Increment(ref batches) == 2)
                    {
                        held.TrySetResult();
                        await release.Task;
                    }
                }
            };

            var import = controller.ImportLancacheManagerAsync(
                new DataMigrationImportRequest { ConnectionString = source, BatchSize = 1 },
                CancellationToken.None);
            await held.Task.WaitAsync(ForceStoppedOwnerHost.Timeout);
            var importId = Assert.Single(tracker.GetActiveOperations(OperationType.DataImport)).Id;

            if (stop == ForceStop)
            {
                // What the card's force stop does to a job without a repair record.
                Assert.True(tracker.ForceKillOperation(importId, followHandoff: false), "The force stop was refused");
                tracker.CompleteOperation(importId, success: false, canceled: true);
            }
            else
            {
                tracker.CancelOperation(importId);
            }

            release.TrySetResult();
            // The import's own request answers as a canceled import that kept what it wrote, and nothing logs an error.
            IActionResult? result = null;
            var answered = await Record.ExceptionAsync(async () => result = await import.WaitAsync(ForceStoppedOwnerHost.Timeout));
            var errors = importLog.Entries.Where(entry => entry.Level >= LogLevel.Error).Select(entry => entry.Message).ToList();
            var answer = (result as OkObjectResult)?.Value as MigrationImportResponse;
            Assert.True(answer?.StageKey == "management.database.import.canceledWithCount" && errors.Count == 0,
                $"After a {stop} the import's own request answered " +
                (answered is null ? $"{result?.GetType().Name} {answer?.StageKey}" : $"{answered.GetType().Name}: {answered.Message}") +
                $" and logged {errors.Count} error(s) ({string.Join(" | ", errors)}); it must answer 200 with " +
                "management.database.import.canceledWithCount and log no error");
        }
        finally
        {
            await DropSourceAsync(source);
        }

        Task? replan = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && replan is null)
        {
            replan = upgrade.LastRun is { } run && run != startupRun ? run : null;
            if (replan is null) await Task.Delay(10);
        }

        await using var check = contexts.CreateDbContext();
        var stored = await check.Downloads.CountAsync();
        Assert.True(replan is not null,
            $"After a {stop} of an import that wrote {stored} rows, the download history upgrade never ran, so the rows stay unmerged");
        await replan.WaitAsync(ForceStoppedOwnerHost.Timeout);
        await using var merged = contexts.CreateDbContext();
        Assert.Equal(1, await merged.Downloads.CountAsync());
    }
}
