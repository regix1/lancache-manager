using System.Data.Common;
using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.HistoryImportSource;

namespace LancacheManager.Tests;

/// <summary>
/// When the app starts, the download history upgrade checks once whether its merge plan still has work. A user
/// force stops a history import while its first rows are landing, before the import has sent any count, and that
/// batch commits anyway and drops the merge plan. Wherever the app-start check stands when the import ends (done,
/// holding a plan read taken before the commit, or not yet started), the imported passes must still be merged
/// into one download per client without a restart.
/// </summary>
public sealed class ImportStoppedDuringStartupPlanReadMergeTests
{
    private const string StartupCheckDoneFirst = "the app-start check finished before the import";
    private const string ImportEndsDuringStartupPlanRead = "the import ended while the app-start check held its plan read";
    private const string ImportEndsBeforeStartupPlanRead = "the import ended before the app-start check read the plan";

    private static readonly DateTime SessionStart = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(StartupCheckDoneFirst)]
    [InlineData(ImportEndsDuringStartupPlanRead)]
    [InlineData(ImportEndsBeforeStartupPlanRead)]
    public async Task AForceStoppedImportIsMergedWhereverTheAppStartCheckStandsAsync(string order)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var context = database.Factory.CreateDbContext())
        {
            // Migrated databases keep this default, which the generated test schema omits.
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Downloads\" ALTER COLUMN \"IsEvicted\" SET DEFAULT FALSE");
        }

        var connectionString = database.ConnectionString;
        var planRead = new PlanReadHold();
        var contexts = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, settings => settings.EnableRetryOnFailure(3, TimeSpan.Zero, null))
            .AddInterceptors(planRead)
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
        // The app start's cleanup, which the app-start check waits for before it reads the plan.
        var startupCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var upgrade = new DownloadHistoryUpgradeService(
            contexts, notifications, tracker, queue, operationState, startupCleanup.Task,
            NullLogger<DownloadHistoryUpgradeService>.Instance, checker);

        // An earlier start already upgraded the empty table, so the plan exists and is empty: no work at this start.
        var first = tracker.RegisterOperation(
            OperationType.DownloadHistoryUpgrade, DownloadHistoryUpgradeService.OperationName, new CancellationTokenSource());
        await upgrade.RunAsync(first, DownloadHistoryUpgradeSql.BatchSize, replan: false, CancellationToken.None);
        tracker.CompleteOperation(first, success: true);

        if (order == ImportEndsDuringStartupPlanRead) planRead.Arm();
        await upgrade.StartAsync(CancellationToken.None);
        if (order == StartupCheckDoneFirst)
        {
            startupCleanup.TrySetResult();
            await upgrade.StartupRequest.WaitAsync(ForceStoppedOwnerHost.Timeout);
        }
        else if (order == ImportEndsDuringStartupPlanRead)
        {
            startupCleanup.TrySetResult();
            await planRead.Held.WaitAsync(ForceStoppedOwnerHost.Timeout);
        }

        // Two passes of one client on one depot, two seconds apart, which the upgrade merges into one download.
        var source = await CreateSourceAsync(connectionString,
        [
            ("10.0.0.9", SessionStart, SessionStart.AddSeconds(1)),
            ("10.0.0.9", SessionStart.AddSeconds(2), SessionStart.AddSeconds(3))
        ]);
        await using var writeGate = new DownloadsWriteGate(connectionString);
        try
        {
            var controller = new DataMigrationController(
                NullLogger<DataMigrationController>.Instance,
                notifications,
                tracker,
                queue,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connectionString })
                    .Build(),
                checker,
                upgrade);

            await writeGate.CloseAsync();
            var import = controller.ImportLancacheManagerAsync(
                new DataMigrationImportRequest { ConnectionString = source, BatchSize = 2 },
                CancellationToken.None);
            await writeGate.WaitForBlockedWriteAsync(ForceStoppedOwnerHost.Timeout);
            var importId = Assert.Single(tracker.GetActiveOperations(OperationType.DataImport)).Id;

            // What the card's force stop does to a job without a repair record: the card ends before any count.
            Assert.True(tracker.ForceKillOperation(importId, followHandoff: false), "The force stop was refused");
            tracker.CompleteOperation(importId, success: false, canceled: true);

            // The held batch commits, drops the merge plan, and the import's own code ends.
            await writeGate.OpenAsync();
            await import.WaitAsync(ForceStoppedOwnerHost.Timeout);
        }
        finally
        {
            await writeGate.OpenAsync();
            await DropSourceAsync(source);
        }

        // The app-start check goes on from wherever it stood.
        planRead.Release();
        startupCleanup.TrySetResult();
        await upgrade.StartupRequest.WaitAsync(ForceStoppedOwnerHost.Timeout);

        Task? merge = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && merge is null)
        {
            merge = upgrade.LastRun;
            if (merge is null) await Task.Delay(10);
        }

        if (merge is not null) await merge.WaitAsync(ForceStoppedOwnerHost.Timeout);
        await using var check = contexts.CreateDbContext();
        var rows = await check.Downloads.Select(download => download.ClientIp).ToListAsync();
        var duplicated = rows.GroupBy(client => client).Where(group => group.Count() > 1)
            .Select(group => $"{group.Count()} rows for {group.Key}").ToList();
        Assert.True(merge is not null && duplicated.Count == 0,
            $"When {order}, the force-stopped import left {rows.Count} download rows ({string.Join(", ", duplicated)}) and " +
            (merge is null
                ? "the download history upgrade never ran, so the imported passes stay unmerged until the next app start"
                : "the upgrade ran") +
            "; the upgrade must merge the imported passes into one download per client");
    }

    /// <summary>
    /// Once armed, holds the next "is the merge plan empty" read after its answer arrives and before the caller acts
    /// on it, until the test lets it go. The server has already answered, so the plan table is not locked meanwhile.
    /// </summary>
    private sealed class PlanReadHold : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Task Held => _held.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _released.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData commandEvent, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(DownloadHistoryUpgradeSql.PlanIsEmpty, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _held.TrySetResult();
                await _released.Task.WaitAsync(ForceStoppedOwnerHost.Timeout, cancellationToken);
            }

            return result;
        }
    }
}
