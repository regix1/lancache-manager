using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace LancacheManager.Tests;

public class DownloadHistoryUpgradeTests
{
    private static readonly DateTime SessionStart =
        new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UpgradeSqlContainsNoInterpolationBraces()
    {
        var statements = new[]
        {
            DownloadHistoryUpgradeSql.DuplicateCheckIndexState,
            DownloadHistoryUpgradeSql.TerminateOrphanIndexBuilds,
            DownloadHistoryUpgradeSql.DropMd5IndexIfExists,
            DownloadHistoryUpgradeSql.CreateMd5Index,
            DownloadHistoryUpgradeSql.DropOldIndexIfExists,
            DownloadHistoryUpgradeSql.RenameMd5Index,
            DownloadHistoryUpgradeSql.PlanExists,
            DownloadHistoryUpgradeSql.PlanIsEmpty,
            DownloadHistoryUpgradeSql.RemainingPlanRows,
            DownloadHistoryUpgradeSql.DropPlan,
            DownloadHistoryUpgradeSql.CreatePlan,
            DownloadHistoryUpgradeSql.PrepareBatch,
            DownloadHistoryUpgradeSql.SkippedInBatch,
            DownloadHistoryUpgradeSql.FoldBatch
        };

        Assert.All(statements, statement =>
        {
            Assert.DoesNotContain('{', statement);
            Assert.DoesNotContain('}', statement);
        });
    }

    [Fact]
    public async Task TransactionOutsideRetriesThrows()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await using (var context = harness.Contexts.CreateDbContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database
                .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanExists)
                .SingleAsync());
        }

        var operationId = harness.RegisterUpgrade();
        var batches = await harness.Service.RunAsync(
            operationId,
            DownloadHistoryUpgradeSql.BatchSize,
            replan: false,
            CancellationToken.None);

        Assert.Equal(0, batches);
        harness.Tracker.CompleteOperation(operationId, success: true);
    }

    [Fact]
    public async Task IndexSwapIsAtomicAndHandlesLongRangeValuesAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        var operationId = harness.RegisterUpgrade();

        await harness.Service.RunAsync(
            operationId,
            DownloadHistoryUpgradeSql.BatchSize,
            replan: false,
            CancellationToken.None);
        harness.Tracker.CompleteOperation(operationId, success: true);

        await using (var context = harness.Contexts.CreateDbContext())
        {
            var definition = await context.Database.SqlQueryRaw<string>("""
                SELECT pg_get_indexdef(c.oid) AS "Value"
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = current_schema()
                  AND c.relname = 'IX_LogEntries_DuplicateCheck'
                """).SingleAsync();
            Assert.Contains("md5(", definition, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("COALESCE", definition, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"HttpRange\"", definition, StringComparison.Ordinal);

            context.LogEntries.Add(NewLogEntry(
                downloadId: null,
                url: CreateLongValue(11),
                range: CreateLongValue(29)));
            await context.SaveChangesAsync();
        }

        await AssertRawSevenColumnIndexRejectsLongValuesAsync(harness.ConnectionString);

        harness.Recorder.Clear();
        await harness.Service.RequestRunAsync();
        Assert.Empty(harness.Tracker.GetActiveOperations(OperationType.DownloadHistoryUpgrade));
        Assert.DoesNotContain(
            harness.Recorder.Commands,
            command => command.Contains("CREATE INDEX", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FiveRowsMergeIntoOneAndKeepEveryLogEntryAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        var ids = await SeedIslandAsync(harness, rows: 5, logsPerRow: 2);
        var operationId = harness.RegisterUpgrade();

        var batches = await harness.Service.RunAsync(
            operationId,
            DownloadHistoryUpgradeSql.BatchSize,
            replan: false,
            CancellationToken.None);

        Assert.Equal(1, batches);
        await using var context = harness.Contexts.CreateDbContext();
        var download = await context.Downloads.SingleAsync();
        Assert.Equal(ids[0], download.Id);
        Assert.Equal(SessionStart, download.StartTimeUtc);
        Assert.Equal(SessionStart.AddSeconds(9), download.EndTimeUtc);
        Assert.Equal(15, download.CacheHitBytes);
        Assert.Equal(30, download.CacheMissBytes);
        Assert.Equal("/chunk/4", download.LastUrl);
        Assert.Equal(10, await context.LogEntries.CountAsync());
        Assert.All(await context.LogEntries.ToListAsync(), entry => Assert.Equal(ids[0], entry.DownloadId));
    }

    [Fact]
    public async Task IdentityAndSessionBoundariesRemainSeparateAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await using (var context = harness.Contexts.CreateDbContext())
        {
            context.Downloads.AddRange(
                NewDownload("10.0.0.1", "steam", SessionStart, 1, "default", false),
                NewDownload("10.0.0.1", "steam", SessionStart.AddSeconds(2), 1, "default", false),
                NewDownload("10.0.0.2", "steam", SessionStart, 1, "default", false),
                NewDownload("10.0.0.2", "steam", SessionStart.AddMinutes(6), 1, "default", false),
                NewDownload("10.0.0.3", "steam", SessionStart, 1, "alpha", false),
                NewDownload("10.0.0.3", "steam", SessionStart.AddSeconds(2), 1, "beta", false),
                NewDownload("10.0.0.4", "steam", SessionStart, 1, "default", false),
                NewDownload("10.0.0.4", "steam", SessionStart.AddSeconds(2), 1, "default", true),
                NewDownload("10.0.0.5", "epicgames", SessionStart, null, "default", false, "/a/b/c/d/e/one"),
                NewDownload("10.0.0.5", "epicgames", SessionStart.AddSeconds(2), null, "default", false, "/q/r/s/t/u/two"));
            await context.SaveChangesAsync();
        }

        var operationId = harness.RegisterUpgrade();
        await harness.Service.RunAsync(operationId, 5000, replan: false, CancellationToken.None);

        await using var check = harness.Contexts.CreateDbContext();
        Assert.Equal(9, await check.Downloads.CountAsync());
        Assert.Equal(1, await check.Downloads.CountAsync(row => row.ClientIp == "10.0.0.1"));
        Assert.Equal(2, await check.Downloads.CountAsync(row => row.ClientIp == "10.0.0.2"));
        Assert.Equal(2, await check.Downloads.CountAsync(row => row.ClientIp == "10.0.0.3"));
        Assert.Equal(2, await check.Downloads.CountAsync(row => row.ClientIp == "10.0.0.4"));
        Assert.Equal(2, await check.Downloads.CountAsync(row => row.ClientIp == "10.0.0.5"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ManualEventTagWinsFromEitherSideAsync(
        bool survivorAutoTagged,
        bool absorbedAutoTagged)
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        var ids = await SeedIslandAsync(harness, rows: 2, logsPerRow: 0);
        await using (var context = harness.SeedContexts.CreateDbContext())
        {
            var eventRow = NewEvent();
            context.Events.Add(eventRow);
            await context.SaveChangesAsync();
            context.EventDownloads.AddRange(
                NewTag(eventRow.Id, ids[0], survivorAutoTagged),
                NewTag(eventRow.Id, ids[1], absorbedAutoTagged));
            await context.SaveChangesAsync();
        }

        var operationId = harness.RegisterUpgrade();
        await harness.Service.RunAsync(operationId, 5000, replan: false, CancellationToken.None);

        await using var check = harness.Contexts.CreateDbContext();
        var tag = await check.EventDownloads.SingleAsync();
        Assert.Equal(ids[0], tag.DownloadId);
        Assert.False(tag.AutoTagged);
    }

    [Fact]
    public async Task AnAbsorbedOnlyTagMovesToTheSurvivorAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        var ids = await SeedIslandAsync(harness, rows: 2, logsPerRow: 0);
        await using (var context = harness.SeedContexts.CreateDbContext())
        {
            var eventRow = NewEvent();
            context.Events.Add(eventRow);
            await context.SaveChangesAsync();
            context.EventDownloads.Add(NewTag(eventRow.Id, ids[1], autoTagged: true));
            await context.SaveChangesAsync();
        }

        var operationId = harness.RegisterUpgrade();
        await harness.Service.RunAsync(operationId, 5000, replan: false, CancellationToken.None);

        await using var check = harness.Contexts.CreateDbContext();
        var tag = await check.EventDownloads.SingleAsync();
        Assert.Equal(ids[0], tag.DownloadId);
        Assert.True(tag.AutoTagged);
    }

    [Fact]
    public async Task BatchSizeDoesNotChangeTheFoldResultAsync()
    {
        var small = await FoldSnapshotAsync(batchSize: 2);
        var large = await FoldSnapshotAsync(batchSize: 5000);
        Assert.Equal(large, small);
    }

    [Fact]
    public async Task CompletionMarkerPreventsAnotherRunAndSignalsInOrderAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await SeedIslandAsync(harness, rows: 2, logsPerRow: 1);
        await harness.Service.StartAsync(CancellationToken.None);
        var run = await WaitForNewRunAsync(harness.Service, previous: null);
        await run;

        await using (var context = harness.Contexts.CreateDbContext())
        {
            Assert.True(await context.Database
                .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanExists)
                .SingleAsync());
            Assert.True(await context.Database
                .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanIsEmpty)
                .SingleAsync());
        }

        var sentEvents = harness.NotificationRecorder.Invocations
            .Where(call => call.Method == nameof(ISignalRNotificationService.NotifyAllAsync))
            .Select(call => (string)call.Args[0]!)
            .ToList();
        Assert.Equal(
            [SignalREvents.DownloadsRefresh, SignalREvents.DownloadHistoryMergeComplete],
            sentEvents);

        harness.Recorder.Clear();
        harness.NotificationRecorder.Invocations.Clear();
        await harness.Service.RequestRunAsync();
        Assert.DoesNotContain(
            harness.Recorder.Commands,
            command => command.Contains("DownloadMergeSlice", StringComparison.Ordinal));
        Assert.Empty(harness.NotificationRecorder.Invocations);
    }

    [Fact]
    public async Task ImportCoveredRowSqlSkipsRowsInsideAnExistingDownloadAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        long existingMaxId;
        await using (var context = harness.Contexts.CreateDbContext())
        {
            var steam = NewDownload("10.0.0.1", "steam", SessionStart, 1, "default", false);
            steam.EndTimeUtc = SessionStart.AddHours(1);
            steam.GameAppId = 101;
            var xbox = NewDownload("10.0.0.2", "xbox", SessionStart.AddHours(2), null, "default", false);
            xbox.EndTimeUtc = SessionStart.AddHours(2).AddMinutes(30);
            var knownApp = NewDownload("10.0.0.3", "steam", SessionStart, null, "default", false);
            knownApp.EndTimeUtc = SessionStart.AddHours(1);
            knownApp.GameAppId = 101;
            var unknownApp = NewDownload("10.0.0.4", "steam", SessionStart, null, "default", false);
            unknownApp.EndTimeUtc = SessionStart.AddHours(1);
            context.Downloads.AddRange(steam, xbox, knownApp, unknownApp);
            await context.SaveChangesAsync();
            existingMaxId = await context.Downloads.MaxAsync(row => row.Id);
        }

        await using var connection = new NpgsqlConnection(harness.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(1, await CoveredRowsAsync(
            connection, "10.0.0.1", "steam", 1, 202, "default", SessionStart.AddMinutes(30), SessionStart.AddMinutes(31), existingMaxId));
        Assert.Equal(0, await CoveredRowsAsync(
            connection, "10.0.0.1", "steam", 1, 101, "default", SessionStart.AddHours(1).AddMinutes(30), SessionStart.AddHours(1).AddMinutes(31), existingMaxId));
        Assert.Equal(0, await CoveredRowsAsync(
            connection, "10.0.0.1", "steam", 2, 101, "default", SessionStart.AddMinutes(30), SessionStart.AddMinutes(31), existingMaxId));
        Assert.Equal(1, await CoveredRowsAsync(
            connection, "10.0.0.1", "steam", 1, 101, "DEFAULT", SessionStart.AddMinutes(30), SessionStart.AddMinutes(31), existingMaxId));
        Assert.Equal(1, await CoveredRowsAsync(
            connection, "10.0.0.2", "wsus", null, null, "default", SessionStart.AddHours(2).AddMinutes(10), SessionStart.AddHours(2).AddMinutes(11), existingMaxId));
        Assert.Equal(1, await CoveredRowsAsync(
            connection, "10.0.0.3", "steam", null, 101, "default", SessionStart.AddMinutes(30), SessionStart.AddMinutes(31), existingMaxId));
        Assert.Equal(0, await CoveredRowsAsync(
            connection, "10.0.0.3", "steam", null, 202, "default", SessionStart.AddMinutes(30), SessionStart.AddMinutes(31), existingMaxId));
        Assert.Equal(0, await CoveredRowsAsync(
            connection, "10.0.0.3", "steam", null, null, "default", SessionStart.AddMinutes(30), SessionStart.AddMinutes(31), existingMaxId));
        Assert.Equal(1, await CoveredRowsAsync(
            connection, "10.0.0.4", "steam", null, null, "default", SessionStart.AddMinutes(30), SessionStart.AddMinutes(31), existingMaxId));
        Assert.Equal(0, await CoveredRowsAsync(
            connection, "10.0.0.4", "steam", null, 101, "default", SessionStart.AddMinutes(30), SessionStart.AddMinutes(31), existingMaxId));
        Assert.Equal(0, await CoveredRowsAsync(
            connection, "10.0.0.3", "steam", null, 101, "archive", SessionStart.AddMinutes(30), SessionStart.AddMinutes(31), existingMaxId));

        await using var exact = new NpgsqlCommand(
            "SELECT COUNT(*) FROM \"Downloads\" WHERE \"ClientIp\" = @clientIp AND \"StartTimeUtc\" = @startTimeUtc",
            connection);
        exact.Parameters.AddWithValue("clientIp", "10.0.0.1");
        exact.Parameters.AddWithValue("startTimeUtc", SessionStart.AddMinutes(30));
        Assert.Equal(0L, (long)(await exact.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ImportBoundaryExcludesRowsInsertedAfterTheImportStartedAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        const long existingMaxId = 0;
        await using (var context = harness.Contexts.CreateDbContext())
        {
            var first = NewDownload("10.0.0.8", "steam", SessionStart, 8, "default", false);
            first.EndTimeUtc = SessionStart.AddMinutes(2);
            context.Downloads.Add(first);
            await context.SaveChangesAsync();
        }

        await using var connection = new NpgsqlConnection(harness.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(0, await CoveredRowsAsync(
            connection,
            "10.0.0.8",
            "steam",
            8,
            null,
            "default",
            SessionStart.AddMinutes(1),
            SessionStart.AddMinutes(1).AddSeconds(30),
            existingMaxId));

        var withoutBoundary = DataMigrationController.ImportCoveredRowSql.Replace(
            "\"Id\" <= @existingMaxId AND ",
            string.Empty,
            StringComparison.Ordinal);
        Assert.NotEqual(DataMigrationController.ImportCoveredRowSql, withoutBoundary);
        Assert.Equal(1, await CoveredRowsAsync(
            connection,
            withoutBoundary,
            "10.0.0.8",
            "steam",
            8,
            null,
            "default",
            SessionStart.AddMinutes(1),
            SessionStart.AddMinutes(1).AddSeconds(30),
            existingMaxId));
    }

    [Fact]
    public async Task RetryImportsTheRemainingIntervalAndAFreshServiceMergesItAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await MarkUpgradeCompleteAsync(harness);

        var first = NewDownload("10.0.0.16", "steam", SessionStart, 16, "default", false);
        first.EndTimeUtc = SessionStart.AddMinutes(10);
        first.CacheHitBytes = 11;
        first.CacheMissBytes = 13;
        var firstResponse = await ImportRowsAsync(harness, [first], batchSize: 1);
        Assert.Equal(1UL, firstResponse.Imported);
        Assert.Equal(0UL, firstResponse.Skipped);

        var second = NewDownload(
            "10.0.0.16",
            "steam",
            SessionStart.AddMinutes(10),
            16,
            "default",
            false);
        second.EndTimeUtc = SessionStart.AddMinutes(20);
        second.CacheHitBytes = 17;
        second.CacheMissBytes = 19;
        var retry = await ImportRowsAsync(harness, [first, second], batchSize: 1);
        Assert.Equal(2UL, retry.TotalRecords);
        Assert.Equal(1UL, retry.Imported);
        Assert.Equal(1UL, retry.Skipped);
        Assert.Equal(0UL, retry.Errors);

        await using (var context = harness.Contexts.CreateDbContext())
        {
            var rows = await context.Downloads.OrderBy(row => row.StartTimeUtc).ToListAsync();
            Assert.Equal(2, rows.Count);
            var eventRow = NewEvent();
            context.Events.Add(eventRow);
            await context.SaveChangesAsync();
            context.EventDownloads.Add(NewTag(eventRow.Id, rows[1].Id, autoTagged: false));
            context.LogEntries.AddRange(
                NewLogEntry(rows[0].Id, "/retry/first", null),
                NewLogEntry(rows[1].Id, "/retry/second", null));
            await context.SaveChangesAsync();
            Assert.False(await context.Database
                .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanExists)
                .SingleAsync());
        }

        using (var service = harness.CreateService(Task.CompletedTask))
        {
            await service.StartAsync(CancellationToken.None);
            var run = await WaitForNewRunAsync(service, previous: null);
            await run;
        }

        long downloadId;
        await using (var merged = harness.Contexts.CreateDbContext())
        {
            var row = await merged.Downloads.SingleAsync();
            downloadId = row.Id;
            Assert.Equal(28, row.CacheHitBytes);
            Assert.Equal(32, row.CacheMissBytes);
            Assert.Equal(2, await merged.LogEntries.CountAsync());
            Assert.All(await merged.LogEntries.ToListAsync(), entry => Assert.Equal(row.Id, entry.DownloadId));
            var tag = await merged.EventDownloads.SingleAsync();
            Assert.Equal(row.Id, tag.DownloadId);
            Assert.False(tag.AutoTagged);
            Assert.True(await merged.Database
                .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanIsEmpty)
                .SingleAsync());
        }

        var repeated = await ImportRowsAsync(harness, [first, second], batchSize: 1);
        Assert.Equal(0UL, repeated.Imported);
        Assert.Equal(2UL, repeated.Skipped);
        Assert.Equal(0UL, repeated.Errors);
        await using var unchanged = harness.Contexts.CreateDbContext();
        var final = await unchanged.Downloads.SingleAsync();
        Assert.Equal(downloadId, final.Id);
        Assert.Equal(28, final.CacheHitBytes);
        Assert.Equal(32, final.CacheMissBytes);
        Assert.Equal(2, await unchanged.LogEntries.CountAsync());
        Assert.False((await unchanged.EventDownloads.SingleAsync()).AutoTagged);
    }

    [Theory]
    [InlineData(5, 20, true)]
    [InlineData(5, 10, false)]
    [InlineData(5, 8, false)]
    public async Task RetryImportsOnlyIntervalsThatCrossTheStoredEndAsync(
        int startMinute,
        int endMinute,
        bool imported)
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        var first = NewDownload("10.0.0.17", "steam", SessionStart, 17, "default", false);
        first.EndTimeUtc = SessionStart.AddMinutes(10);
        first.CacheHitBytes = 11;
        first.CacheMissBytes = 13;
        Assert.Equal(1UL, (await ImportRowsAsync(harness, [first], batchSize: 1)).Imported);

        var second = NewDownload(
            "10.0.0.17",
            "steam",
            SessionStart.AddMinutes(startMinute),
            17,
            "default",
            false);
        second.EndTimeUtc = SessionStart.AddMinutes(endMinute);
        second.CacheHitBytes = 17;
        second.CacheMissBytes = 19;
        var response = await ImportRowsAsync(harness, [first, second], batchSize: 1);

        Assert.Equal(imported ? 1UL : 0UL, response.Imported);
        Assert.Equal(imported ? 1UL : 2UL, response.Skipped);
        Assert.Equal(0UL, response.Errors);
        await using var check = harness.Contexts.CreateDbContext();
        Assert.Equal(imported ? 2 : 1, await check.Downloads.CountAsync());
        Assert.Equal(imported ? 28 : 11, await check.Downloads.SumAsync(row => row.CacheHitBytes));
        Assert.Equal(imported ? 32 : 13, await check.Downloads.SumAsync(row => row.CacheMissBytes));
    }

    [Fact]
    public async Task InterruptedImportLeavesCommittedRowsForAFreshServiceAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await MarkUpgradeCompleteAsync(harness);
        var rows = Enumerable.Range(0, 3)
            .Select(index =>
            {
                var row = NewDownload(
                    "10.0.0.18",
                    "steam",
                    SessionStart.AddMinutes(index),
                    18,
                    "default",
                    false);
                row.EndTimeUtc = SessionStart.AddMinutes(index + 1);
                row.CacheHitBytes = index + 1;
                row.CacheMissBytes = (index + 1) * 2;
                return row;
            })
            .ToList();

        await Assert.ThrowsAsync<IOException>(() => ImportRowsAsync(
            harness,
            rows,
            batchSize: 2,
            batchCommitted: () => throw new IOException("Import stopped after a committed batch")));

        await using (var committed = harness.Contexts.CreateDbContext())
        {
            var stored = await committed.Downloads.OrderBy(row => row.StartTimeUtc).ToListAsync();
            Assert.Equal(2, stored.Count);
            var eventRow = NewEvent();
            committed.Events.Add(eventRow);
            await committed.SaveChangesAsync();
            committed.EventDownloads.Add(NewTag(eventRow.Id, stored[1].Id, autoTagged: false));
            committed.LogEntries.AddRange(
                NewLogEntry(stored[0].Id, "/interrupted/first", null),
                NewLogEntry(stored[1].Id, "/interrupted/second", null));
            await committed.SaveChangesAsync();
            Assert.False(await committed.Database
                .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanExists)
                .SingleAsync());
        }

        using (var service = harness.CreateService(Task.CompletedTask))
        {
            await service.StartAsync(CancellationToken.None);
            var run = await WaitForNewRunAsync(service, previous: null);
            await run;
        }

        await using var check = harness.Contexts.CreateDbContext();
        var combined = await check.Downloads.SingleAsync();
        Assert.Equal(3, combined.CacheHitBytes);
        Assert.Equal(6, combined.CacheMissBytes);
        Assert.Equal(2, await check.LogEntries.CountAsync());
        Assert.All(await check.LogEntries.ToListAsync(), entry => Assert.Equal(combined.Id, entry.DownloadId));
        var tag = await check.EventDownloads.SingleAsync();
        Assert.Equal(combined.Id, tag.DownloadId);
        Assert.False(tag.AutoTagged);
    }

    [Fact]
    public async Task FailedImportCommitRestoresTheCompletedMarkerAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await MarkUpgradeCompleteAsync(harness);
        await using (var connection = new NpgsqlConnection(harness.ConnectionString))
        {
            await connection.OpenAsync();
            await using var trigger = new NpgsqlCommand("""
                CREATE FUNCTION "FailImportCommit"() RETURNS trigger
                LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."ClientIp" = '10.0.0.19' THEN
                        RAISE EXCEPTION 'injected import commit failure';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                CREATE CONSTRAINT TRIGGER "FailImportCommitTrigger"
                AFTER INSERT ON "Downloads"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION "FailImportCommit"();
                """, connection);
            await trigger.ExecuteNonQueryAsync();
        }

        var row = NewDownload("10.0.0.19", "steam", SessionStart, 19, "default", false);
        await Assert.ThrowsAsync<PostgresException>(() => ImportRowsAsync(harness, [row], batchSize: 1));

        await using var check = harness.Contexts.CreateDbContext();
        Assert.Empty(await check.Downloads.ToListAsync());
        Assert.True(await check.Database
            .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanExists)
            .SingleAsync());
        Assert.True(await check.Database
            .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanIsEmpty)
            .SingleAsync());
    }

    [Fact]
    public async Task SkippedImportKeepsTheCompletedMarkerAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        var existing = NewDownload("10.0.0.20", "steam", SessionStart, 20, "default", false);
        existing.EndTimeUtc = SessionStart.AddMinutes(10);
        await using (var seed = harness.Contexts.CreateDbContext())
        {
            seed.Downloads.Add(existing);
            await seed.SaveChangesAsync();
        }
        await MarkUpgradeCompleteAsync(harness);

        var contained = NewDownload(
            "10.0.0.20",
            "steam",
            SessionStart.AddMinutes(2),
            20,
            "default",
            false);
        contained.EndTimeUtc = SessionStart.AddMinutes(8);
        var response = await ImportRowsAsync(harness, [contained]);
        Assert.Equal(0UL, response.Imported);
        Assert.Equal(1UL, response.Skipped);

        harness.NotificationRecorder.Invocations.Clear();
        using var service = harness.CreateService(Task.CompletedTask);
        await service.RequestRunAsync();
        Assert.Null(service.LastRun);
        Assert.Empty(harness.Tracker.GetActiveOperations(OperationType.DownloadHistoryUpgrade));
        Assert.Empty(harness.Tracker.GetWaitingOperations());
        Assert.Empty(MergeEvents(harness));
        await using var check = harness.Contexts.CreateDbContext();
        Assert.True(await check.Database
            .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanIsEmpty)
            .SingleAsync());
    }

    [Theory]
    [InlineData(1000, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task ImportKeepsSameRunRowsUntilTheUpgradeFoldsThemAsync(
        int batchSize,
        bool overlapping)
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await MarkUpgradeCompleteAsync(harness);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        harness.Recorder.OnExecuting = command =>
        {
            if (command.CommandText.Contains("WITH members AS", StringComparison.Ordinal)
                && Interlocked.Exchange(ref held, 1) == 0)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        };
        await harness.Service.StartAsync(CancellationToken.None);

        var first = NewDownload("10.0.0.8", "steam", SessionStart, 8, "default", false);
        first.EndTimeUtc = overlapping
            ? SessionStart.AddMinutes(3)
            : SessionStart.AddMinutes(1);
        first.CacheHitBytes = 11;
        first.CacheMissBytes = 13;
        var second = NewDownload(
            "10.0.0.8",
            "steam",
            SessionStart.AddMinutes(1),
            8,
            "default",
            false);
        second.CacheHitBytes = 17;
        second.CacheMissBytes = 19;

        Task? run = null;
        try
        {
            var response = await ImportRowsAsync(harness, [first, second], batchSize);
            Assert.Equal(2UL, response.TotalRecords);
            Assert.Equal(2UL, response.Imported);
            Assert.Equal(0UL, response.Skipped);
            Assert.Equal(0UL, response.Errors);

            run = await WaitForNewRunAsync(harness.Service, previous: null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await using (var beforeFold = harness.Contexts.CreateDbContext())
            {
                var rows = await beforeFold.Downloads.ToListAsync();
                Assert.Equal(2, rows.Count);
                Assert.Equal(28, rows.Sum(row => row.CacheHitBytes));
                Assert.Equal(32, rows.Sum(row => row.CacheMissBytes));
            }
        }
        finally
        {
            release.TrySetResult();
        }

        await run!;
        await using var afterFold = harness.Contexts.CreateDbContext();
        var combined = await afterFold.Downloads.SingleAsync();
        Assert.Equal(28, combined.CacheHitBytes);
        Assert.Equal(32, combined.CacheMissBytes);
    }

    [Fact]
    public async Task ImportSkipsAnOlderPassInsideAPreexistingMergedDownloadAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        long downloadId;
        long eventId;
        await using (var context = harness.Contexts.CreateDbContext())
        {
            var merged = NewDownload("10.0.0.9", "steam", SessionStart, 9, "default", false);
            merged.EndTimeUtc = SessionStart.AddHours(1);
            merged.CacheHitBytes = 101;
            merged.CacheMissBytes = 202;
            var eventRow = NewEvent();
            context.Downloads.Add(merged);
            context.Events.Add(eventRow);
            await context.SaveChangesAsync();
            context.EventDownloads.Add(NewTag(eventRow.Id, merged.Id, autoTagged: false));
            context.LogEntries.Add(NewLogEntry(merged.Id, "/merged/history", null));
            await context.SaveChangesAsync();
            downloadId = merged.Id;
            eventId = eventRow.Id;
        }

        var oldPass = NewDownload(
            "10.0.0.9",
            "steam",
            SessionStart.AddMinutes(10),
            9,
            "default",
            false);
        oldPass.CacheHitBytes = 7;
        oldPass.CacheMissBytes = 11;
        var response = await ImportRowsAsync(harness, [oldPass]);

        Assert.Equal(1UL, response.TotalRecords);
        Assert.Equal(0UL, response.Imported);
        Assert.Equal(1UL, response.Skipped);
        Assert.Equal(0UL, response.Errors);
        await using var check = harness.Contexts.CreateDbContext();
        var unchanged = await check.Downloads.SingleAsync();
        Assert.Equal(downloadId, unchanged.Id);
        Assert.Equal(SessionStart.AddHours(1), unchanged.EndTimeUtc);
        Assert.Equal(101, unchanged.CacheHitBytes);
        Assert.Equal(202, unchanged.CacheMissBytes);
        var tag = await check.EventDownloads.SingleAsync();
        Assert.Equal(eventId, tag.EventId);
        Assert.Equal(downloadId, tag.DownloadId);
        Assert.False(tag.AutoTagged);
        Assert.Equal(downloadId, (await check.LogEntries.SingleAsync()).DownloadId);
    }

    [Fact]
    public async Task ImportUsesGameAppIdForDownloadsWithoutADepotAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        long existingMaxId;
        await using (var context = harness.Contexts.CreateDbContext())
        {
            var existing = NewDownload("10.0.0.10", "steam", SessionStart, null, "default", false);
            existing.EndTimeUtc = SessionStart.AddHours(1);
            existing.GameAppId = 101;
            context.Downloads.Add(existing);
            await context.SaveChangesAsync();
            existingMaxId = existing.Id;
        }

        await using (var connection = new NpgsqlConnection(harness.ConnectionString))
        {
            await connection.OpenAsync();
            Assert.Equal(0, await CoveredRowsAsync(
                connection,
                "10.0.0.10",
                "steam",
                null,
                202,
                "default",
                SessionStart.AddMinutes(10),
                SessionStart.AddMinutes(11),
                existingMaxId));
            var withoutAppIdentity = DataMigrationController.ImportCoveredRowSql.Replace(
                "(\"DepotId\" IS NOT NULL OR \"GameAppId\" IS NOT DISTINCT FROM @gameAppId) AND ",
                string.Empty,
                StringComparison.Ordinal);
            Assert.NotEqual(DataMigrationController.ImportCoveredRowSql, withoutAppIdentity);
            Assert.Equal(1, await CoveredRowsAsync(
                connection,
                withoutAppIdentity,
                "10.0.0.10",
                "steam",
                null,
                202,
                "default",
                SessionStart.AddMinutes(10),
                SessionStart.AddMinutes(11),
                existingMaxId));
        }

        var app202 = NewDownload(
            "10.0.0.10", "steam", SessionStart.AddMinutes(10), null, "default", false);
        app202.GameAppId = 202;
        var differentApp = await ImportRowsAsync(harness, [app202]);
        Assert.Equal(1UL, differentApp.Imported);
        Assert.Equal(0UL, differentApp.Skipped);

        var app101 = NewDownload(
            "10.0.0.10", "steam", SessionStart.AddMinutes(20), null, "default", false);
        app101.GameAppId = 101;
        var matchingApp = await ImportRowsAsync(harness, [app101]);
        Assert.Equal(0UL, matchingApp.Imported);
        Assert.Equal(1UL, matchingApp.Skipped);

        var unknownApp = NewDownload(
            "10.0.0.10", "steam", SessionStart.AddMinutes(30), null, "default", false);
        unknownApp.EndTimeUtc = SessionStart.AddHours(1);
        var unknownAgainstKnown = await ImportRowsAsync(harness, [unknownApp]);
        Assert.Equal(1UL, unknownAgainstKnown.Imported);
        Assert.Equal(0UL, unknownAgainstKnown.Skipped);

        var app303 = NewDownload(
            "10.0.0.10", "steam", SessionStart.AddMinutes(40), null, "default", false);
        app303.GameAppId = 303;
        var knownAgainstUnknown = await ImportRowsAsync(harness, [app303]);
        Assert.Equal(1UL, knownAgainstUnknown.Imported);
        Assert.Equal(0UL, knownAgainstUnknown.Skipped);

        await using var check = harness.Contexts.CreateDbContext();
        Assert.Equal(4, await check.Downloads.CountAsync());
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1)]
    public async Task OverwriteKeepsMergedHistoryAcrossRepeatedImportsAsync(int batchSize)
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        long downloadId;
        long eventId;
        await using (var context = harness.Contexts.CreateDbContext())
        {
            var merged = NewDownload("10.0.0.11", "steam", SessionStart, 11, "default", false);
            merged.EndTimeUtc = SessionStart.AddMinutes(10);
            merged.CacheHitBytes = 100;
            merged.CacheMissBytes = 200;
            var eventRow = NewEvent();
            context.Downloads.Add(merged);
            context.Events.Add(eventRow);
            await context.SaveChangesAsync();
            context.EventDownloads.Add(NewTag(eventRow.Id, merged.Id, autoTagged: false));
            context.LogEntries.Add(NewLogEntry(merged.Id, "/merged/overwrite-history", null));
            await context.SaveChangesAsync();
            downloadId = merged.Id;
            eventId = eventRow.Id;
        }

        await MarkUpgradeCompleteAsync(harness);
        await harness.Service.StartAsync(CancellationToken.None);
        var firstPass = NewDownload("10.0.0.11", "steam", SessionStart, 11, "default", false);
        firstPass.EndTimeUtc = SessionStart.AddMinutes(2);
        firstPass.CacheHitBytes = 10;
        firstPass.CacheMissBytes = 20;
        var secondPass = NewDownload(
            "10.0.0.11",
            "steam",
            SessionStart.AddMinutes(1),
            11,
            "default",
            false);
        secondPass.EndTimeUtc = SessionStart.AddMinutes(3);
        secondPass.CacheHitBytes = 30;
        secondPass.CacheMissBytes = 40;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await ImportRowsAsync(
                harness,
                [firstPass, secondPass],
                batchSize,
                overwriteExisting: true);
            Assert.Equal(2UL, response.TotalRecords);
            Assert.Equal(0UL, response.Imported);
            Assert.Equal(2UL, response.Skipped);
            Assert.Equal(0UL, response.Errors);
            Assert.Null(harness.Service.LastRun);
        }

        await using var check = harness.Contexts.CreateDbContext();
        var unchanged = await check.Downloads.SingleAsync();
        Assert.Equal(downloadId, unchanged.Id);
        Assert.Equal(SessionStart.AddMinutes(10), unchanged.EndTimeUtc);
        Assert.Equal(100, unchanged.CacheHitBytes);
        Assert.Equal(200, unchanged.CacheMissBytes);
        var tag = await check.EventDownloads.SingleAsync();
        Assert.Equal(eventId, tag.EventId);
        Assert.Equal(downloadId, tag.DownloadId);
        Assert.False(tag.AutoTagged);
        Assert.Equal(downloadId, (await check.LogEntries.SingleAsync()).DownloadId);
    }

    [Fact]
    public async Task OverwriteKeepsEqualEndMergedByteTotalsAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        long downloadId;
        await using (var context = harness.Contexts.CreateDbContext())
        {
            var merged = NewDownload("10.0.0.12", "steam", SessionStart, 12, "default", false);
            merged.EndTimeUtc = SessionStart.AddMinutes(2);
            merged.CacheHitBytes = 40;
            merged.CacheMissBytes = 60;
            context.Downloads.Add(merged);
            await context.SaveChangesAsync();
            downloadId = merged.Id;
        }

        var firstPass = NewDownload("10.0.0.12", "steam", SessionStart, 12, "default", false);
        firstPass.EndTimeUtc = SessionStart.AddMinutes(2);
        firstPass.CacheHitBytes = 10;
        firstPass.CacheMissBytes = 20;
        var secondPass = NewDownload(
            "10.0.0.12",
            "steam",
            SessionStart.AddMinutes(1),
            12,
            "default",
            false);
        secondPass.EndTimeUtc = SessionStart.AddMinutes(2);
        secondPass.CacheHitBytes = 30;
        secondPass.CacheMissBytes = 40;
        var response = await ImportRowsAsync(
            harness,
            [firstPass, secondPass],
            overwriteExisting: true);

        Assert.Equal(0UL, response.Imported);
        Assert.Equal(2UL, response.Skipped);
        Assert.Equal(0UL, response.Errors);
        await using var check = harness.Contexts.CreateDbContext();
        var unchanged = await check.Downloads.SingleAsync();
        Assert.Equal(downloadId, unchanged.Id);
        Assert.Equal(SessionStart.AddMinutes(2), unchanged.EndTimeUtc);
        Assert.Equal(40, unchanged.CacheHitBytes);
        Assert.Equal(60, unchanged.CacheMissBytes);
    }

    [Theory]
    [InlineData(20, 10, 10, 20)]
    [InlineData(10, 20, 20, 10)]
    public async Task OverwriteRequiresEachByteComponentToStayNondecreasingAsync(
        long targetHit,
        long targetMiss,
        long sourceHit,
        long sourceMiss)
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await using (var context = harness.Contexts.CreateDbContext())
        {
            var existing = NewDownload("10.0.0.13", "steam", SessionStart, 13, "default", false);
            existing.EndTimeUtc = SessionStart.AddMinutes(2);
            existing.CacheHitBytes = targetHit;
            existing.CacheMissBytes = targetMiss;
            context.Downloads.Add(existing);
            await context.SaveChangesAsync();
        }

        var source = NewDownload("10.0.0.13", "steam", SessionStart, 13, "default", false);
        source.EndTimeUtc = SessionStart.AddMinutes(3);
        source.CacheHitBytes = sourceHit;
        source.CacheMissBytes = sourceMiss;
        var response = await ImportRowsAsync(harness, [source], overwriteExisting: true);

        Assert.Equal(0UL, response.Imported);
        Assert.Equal(1UL, response.Skipped);
        Assert.Equal(0UL, response.Errors);
        await using var check = harness.Contexts.CreateDbContext();
        var unchanged = await check.Downloads.SingleAsync();
        Assert.Equal(targetHit, unchanged.CacheHitBytes);
        Assert.Equal(targetMiss, unchanged.CacheMissBytes);
    }

    [Fact]
    public async Task OverwriteAcceptsASourceSnapshotThatCoversTheStoredHistoryAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        long downloadId;
        long eventId;
        await using (var context = harness.Contexts.CreateDbContext())
        {
            var existing = NewDownload("10.0.0.14", "steam", SessionStart, 14, "default", false);
            existing.EndTimeUtc = SessionStart.AddMinutes(1);
            existing.CacheHitBytes = 10;
            existing.CacheMissBytes = 20;
            var eventRow = NewEvent();
            context.Downloads.Add(existing);
            context.Events.Add(eventRow);
            await context.SaveChangesAsync();
            context.EventDownloads.Add(NewTag(eventRow.Id, existing.Id, autoTagged: false));
            await context.SaveChangesAsync();
            downloadId = existing.Id;
            eventId = eventRow.Id;
        }
        await MarkUpgradeCompleteAsync(harness);

        var source = NewDownload("10.0.0.14", "steam", SessionStart, 14, "default", false);
        source.EndTimeUtc = SessionStart.AddMinutes(2);
        source.CacheHitBytes = 11;
        source.CacheMissBytes = 25;
        var response = await ImportRowsAsync(harness, [source], overwriteExisting: true);

        Assert.Equal(1UL, response.Imported);
        Assert.Equal(0UL, response.Skipped);
        Assert.Equal(0UL, response.Errors);
        await using var check = harness.Contexts.CreateDbContext();
        var updated = await check.Downloads.SingleAsync();
        Assert.Equal(downloadId, updated.Id);
        Assert.Equal(source.EndTimeUtc, updated.EndTimeUtc);
        Assert.Equal(source.CacheHitBytes, updated.CacheHitBytes);
        Assert.Equal(source.CacheMissBytes, updated.CacheMissBytes);
        var tag = await check.EventDownloads.SingleAsync();
        Assert.Equal(eventId, tag.EventId);
        Assert.Equal(downloadId, tag.DownloadId);
        Assert.False(tag.AutoTagged);
        Assert.False(await check.Database
            .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanExists)
            .SingleAsync());
    }

    [Fact]
    public async Task WeakerOverwritePredicatesAllowStoredHistoryToShrinkAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await using var connection = new NpgsqlConnection(harness.ConnectionString);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand("""
            CREATE TEMP TABLE "OverwritePredicateCase" (
                "ClientIp" text NOT NULL,
                "StartTimeUtc" timestamptz NOT NULL,
                "EndTimeUtc" timestamptz NOT NULL,
                "CacheHitBytes" bigint NOT NULL,
                "CacheMissBytes" bigint NOT NULL
            );
            INSERT INTO "OverwritePredicateCase"
                ("ClientIp", "StartTimeUtc", "EndTimeUtc", "CacheHitBytes", "CacheMissBytes")
            VALUES ('10.0.0.15', @start, @laterEnd, 100, 200);
            """, connection))
        {
            create.Parameters.AddWithValue("start", SessionStart);
            create.Parameters.AddWithValue("laterEnd", SessionStart.AddMinutes(10));
            await create.ExecuteNonQueryAsync();
        }

        await using (var unguarded = new NpgsqlCommand("""
            UPDATE "OverwritePredicateCase"
            SET "EndTimeUtc" = @sourceEnd, "CacheHitBytes" = 10, "CacheMissBytes" = 20
            WHERE "ClientIp" = '10.0.0.15' AND "StartTimeUtc" = @start;
            SELECT "EndTimeUtc" FROM "OverwritePredicateCase";
            """, connection))
        {
            unguarded.Parameters.AddWithValue("start", SessionStart);
            unguarded.Parameters.AddWithValue("sourceEnd", SessionStart.AddMinutes(2));
            Assert.Equal(SessionStart.AddMinutes(2), (DateTime)(await unguarded.ExecuteScalarAsync())!);
        }

        await using (var endOnly = new NpgsqlCommand("""
            UPDATE "OverwritePredicateCase"
            SET "EndTimeUtc" = @sourceEnd, "CacheHitBytes" = 10, "CacheMissBytes" = 20
            WHERE "ClientIp" = '10.0.0.15'
              AND "StartTimeUtc" = @start
              AND "EndTimeUtc" <= @sourceEnd;
            SELECT "CacheHitBytes" FROM "OverwritePredicateCase";
            """, connection))
        {
            endOnly.Parameters.AddWithValue("start", SessionStart);
            endOnly.Parameters.AddWithValue("sourceEnd", SessionStart.AddMinutes(2));
            await using var reset = new NpgsqlCommand("""
                UPDATE "OverwritePredicateCase"
                SET "EndTimeUtc" = @sourceEnd, "CacheHitBytes" = 40, "CacheMissBytes" = 60
                """, connection);
            reset.Parameters.AddWithValue("sourceEnd", SessionStart.AddMinutes(2));
            await reset.ExecuteNonQueryAsync();
            Assert.Equal(10L, (long)(await endOnly.ExecuteScalarAsync())!);
        }

        await using (var combinedTotal = new NpgsqlCommand("""
            UPDATE "OverwritePredicateCase"
            SET "EndTimeUtc" = @sourceEnd, "CacheHitBytes" = 10, "CacheMissBytes" = 40
            WHERE "ClientIp" = '10.0.0.15'
              AND "StartTimeUtc" = @start
              AND "EndTimeUtc" <= @sourceEnd
              AND "CacheHitBytes" + "CacheMissBytes" <= 10 + 40;
            SELECT "CacheHitBytes" FROM "OverwritePredicateCase";
            """, connection))
        {
            combinedTotal.Parameters.AddWithValue("start", SessionStart);
            combinedTotal.Parameters.AddWithValue("sourceEnd", SessionStart.AddMinutes(3));
            await using var reset = new NpgsqlCommand("""
                UPDATE "OverwritePredicateCase"
                SET "EndTimeUtc" = @targetEnd, "CacheHitBytes" = 20, "CacheMissBytes" = 10
                """, connection);
            reset.Parameters.AddWithValue("targetEnd", SessionStart.AddMinutes(2));
            await reset.ExecuteNonQueryAsync();
            Assert.Equal(10L, (long)(await combinedTotal.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task TheUpgradeWaitsForRunningWorkAndHoldsTheSlotWhileItRuns()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await SeedIslandAsync(harness, rows: 2, logsPerRow: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        harness.Recorder.OnExecuting = command =>
        {
            if (command.CommandText.Contains("WITH members AS", StringComparison.Ordinal)
                && Interlocked.Exchange(ref held, 1) == 0)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        };

        var blockerId = harness.Tracker.RegisterOperation(
            OperationType.LogProcessing,
            "Log processing",
            new CancellationTokenSource());
        await harness.Service.RequestRunAsync();

        var waiting = Assert.Single(harness.Tracker.GetWaitingOperations());
        Assert.Equal(OperationType.DownloadHistoryUpgrade, waiting.Type);
        Assert.Equal(DownloadHistoryUpgradeService.OperationName, waiting.Name);
        Assert.DoesNotContain(
            harness.Recorder.Commands,
            command => command.Contains("WITH members AS", StringComparison.Ordinal));

        harness.Tracker.CompleteOperation(blockerId, success: true);
        var run = await WaitForNewRunAsync(harness.Service, previous: null);
        await entered.Task;

        var liveConflict = await harness.Checker.CheckAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            CancellationToken.None);
        Assert.NotNull(liveConflict);
        Assert.Equal("errors.conflict.downloadHistoryUpgradeActive", liveConflict!.StageKey);
        Assert.False(LiveLogMonitorService.CanBypassConflictForIncrementalIngestion(liveConflict, 1));

        var importConflict = await harness.Checker.CheckAsync(
            OperationType.DataImport,
            ConflictScope.Bulk(),
            CancellationToken.None);
        Assert.Equal("errors.conflict.downloadHistoryUpgradeActive", importConflict!.StageKey);

        var removalStarted = new TaskCompletionSource<Guid>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedRemoval = await harness.Queue.EnqueueAsync(
            OperationType.GameRemoval,
            ConflictScope.NamedGame("blizzard", "Diablo IV"),
            "Remove Diablo IV",
            () =>
            {
                var id = harness.Tracker.RegisterOperation(
                    OperationType.GameRemoval,
                    "Remove Diablo IV",
                    new CancellationTokenSource());
                removalStarted.TrySetResult(id);
                return Task.FromResult<Guid?>(id);
            },
            CancellationToken.None);
        Assert.True(queuedRemoval.Queued);

        release.TrySetResult();
        await run;
        Assert.NotEqual(Guid.Empty, await removalStarted.Task);
    }

    [Fact]
    public async Task TheUpgradeWaitsForTheStartupCleanup()
    {
        await using var harness = await UpgradeHarness.CreateAsync(
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        await SeedIslandAsync(harness, rows: 2, logsPerRow: 0);
        harness.Recorder.Clear();

        await harness.Service.StartAsync(CancellationToken.None);
        var executeTask = harness.Service.ExecuteTask;
        Assert.NotNull(executeTask);
        await executeTask;
        Assert.Null(harness.Service.LastRun);
        Assert.Empty(harness.Tracker.GetActiveOperations(OperationType.DownloadHistoryUpgrade));
        Assert.Empty(harness.Recorder.Commands);

        harness.StartupCleanup.TrySetResult();
        var run = await WaitForNewRunAsync(harness.Service, previous: null);
        await run;
        Assert.Contains(
            harness.Tracker.GetRuns().Runs,
            row => row.OperationType == OperationType.DownloadHistoryUpgrade.ToWireString()
                && row.Status == "completed");
    }

    [Fact]
    public async Task ImportRequestWaitsForCleanupBeforeDatabaseOrQueueWorkAsync()
    {
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PrefillProgressLoginPhaseGuardTests.BlockingDbContextFactory? contexts = null;
        await using var harness = await UpgradeHarness.CreateAsync(
            cleanup,
            options => contexts = new PrefillProgressLoginPhaseGuardTests.BlockingDbContextFactory(options));
        await SeedIslandAsync(harness, rows: 2, logsPerRow: 0);
        harness.Recorder.Clear();
        await harness.Service.StartAsync(CancellationToken.None);
        var executeTask = harness.Service.ExecuteTask;
        Assert.NotNull(executeTask);
        await executeTask;

        var importId = harness.Tracker.RegisterOperation(
            OperationType.DataImport,
            "Historical import",
            new CancellationTokenSource(),
            new DataImportMetrics { RecordsImported = 2 });
        var terminalObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTerminal(OperationInfo operation)
        {
            if (operation.Id == importId)
            {
                terminalObserved.TrySetResult();
            }
        }
        harness.Tracker.OperationTerminal += OnTerminal;
        try
        {
            harness.Tracker.CompleteOperation(importId, success: true);
            await terminalObserved.Task;
        }
        finally
        {
            harness.Tracker.OperationTerminal -= OnTerminal;
        }

        Assert.Equal(0, contexts!.AsyncCreateCount);
        Assert.Empty(harness.Recorder.Commands);
        Assert.Empty(harness.Tracker.GetActiveOperations(OperationType.DownloadHistoryUpgrade));
        Assert.Empty(harness.Tracker.GetWaitingOperations());

        cleanup.TrySetResult();
        await contexts.FirstCreateStarted.Task;
        Assert.Equal(1, contexts.AsyncCreateCount);
        contexts.ReleaseFirstCreate.TrySetResult(true);
        var run = await WaitForNewRunAsync(harness.Service, previous: null);
        await run;

        await using var check = harness.Contexts.CreateDbContext();
        Assert.Single(await check.Downloads.ToListAsync());
        Assert.Single(
            harness.Tracker.GetRuns().Runs,
            row => row.OperationType == OperationType.DownloadHistoryUpgrade.ToWireString());
    }

    [Fact]
    public async Task ShutdownSettlesARequestWaitingForCleanupAsync()
    {
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PrefillProgressLoginPhaseGuardTests.BlockingDbContextFactory? contexts = null;
        await using var harness = await UpgradeHarness.CreateAsync(
            cleanup,
            options => contexts = new PrefillProgressLoginPhaseGuardTests.BlockingDbContextFactory(options));
        using var service = harness.CreateService(cleanup.Task);
        var initialRequest = service.StartupRequest;
        Assert.True(initialRequest.IsCompletedSuccessfully);
        var startupRequest = initialRequest;

        try
        {
            await service.StartAsync(CancellationToken.None);
            var executeTask = service.ExecuteTask;
            Assert.NotNull(executeTask);
            await executeTask;

            startupRequest = service.StartupRequest;
            Assert.NotSame(initialRequest, startupRequest);
            Assert.False(startupRequest.IsCompleted);
            Assert.False(cleanup.Task.IsCompleted);
            Assert.Equal(0, contexts!.AsyncCreateCount);
            Assert.Empty(harness.Recorder.Commands);
            Assert.Empty(harness.Tracker.GetActiveOperations(OperationType.DownloadHistoryUpgrade));
            Assert.Empty(harness.Tracker.GetWaitingOperations());
            Assert.Null(service.LastRun);

            await service.StopAsync(CancellationToken.None);
            await startupRequest;

            Assert.True(startupRequest.IsCompletedSuccessfully);
            Assert.False(cleanup.Task.IsCompleted);
            Assert.Equal(0, contexts.AsyncCreateCount);
            Assert.Empty(harness.Recorder.Commands);
            Assert.Empty(harness.Tracker.GetActiveOperations(OperationType.DownloadHistoryUpgrade));
            Assert.Empty(harness.Tracker.GetWaitingOperations());
            Assert.Null(service.LastRun);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await startupRequest;
        }
    }

    [Fact]
    public async Task CancelledStartDoesNotEnterTheServiceAsync()
    {
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PrefillProgressLoginPhaseGuardTests.BlockingDbContextFactory? contexts = null;
        await using var harness = await UpgradeHarness.CreateAsync(
            cleanup,
            options => contexts = new PrefillProgressLoginPhaseGuardTests.BlockingDbContextFactory(options));
        var logger = new CapturingLogger<DownloadHistoryUpgradeService>();
        using var service = harness.CreateService(cleanup.Task, logger);
        var initialRequest = service.StartupRequest;
        Assert.True(initialRequest.IsCompletedSuccessfully);
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();

        try
        {
            await service.StartAsync(stopping.Token);
            await service.StopAsync(CancellationToken.None);

            var executeTask = service.ExecuteTask;
            Assert.NotNull(executeTask);
            Assert.True(executeTask.IsCanceled);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executeTask);
            await initialRequest;

            Assert.True(initialRequest.IsCompletedSuccessfully);
            Assert.Same(initialRequest, service.StartupRequest);
            Assert.False(cleanup.Task.IsCompleted);
            Assert.Equal(0, contexts!.AsyncCreateCount);
            Assert.Empty(harness.Recorder.Commands);
            Assert.Empty(harness.Tracker.GetActiveOperations(OperationType.DownloadHistoryUpgrade));
            Assert.Empty(harness.Tracker.GetWaitingOperations());
            Assert.Null(service.LastRun);
            Assert.Empty(logger.Entries);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await service.StartupRequest;
        }
    }

    [Fact]
    public async Task AFailedRunKeepsOneCardWithItsReason()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await SeedIslandAsync(harness, rows: 2, logsPerRow: 0);
        harness.Recorder.OnExecuting = command =>
        {
            if (command.CommandText.Contains("WITH members AS", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("boom");
            }
        };

        await harness.Service.RequestRunAsync();
        var first = await WaitForNewRunAsync(harness.Service, previous: null);
        await first;
        Assert.Empty(MergeEvents(harness));
        var firstFailure = Assert.Single(FailedUpgradeRuns(harness));
        Assert.StartsWith("Download history upgrade stopped: boom.", firstFailure.Error!);
        Assert.Contains("rows still wait to merge", firstFailure.Error!);

        await harness.Service.RequestRunAsync();
        var second = await WaitForNewRunAsync(harness.Service, first);
        await second;
        Assert.Single(FailedUpgradeRuns(harness));

        harness.Recorder.OnExecuting = null;
        await harness.Service.RequestRunAsync();
        var third = await WaitForNewRunAsync(harness.Service, second);
        await third;
        Assert.Empty(FailedUpgradeRuns(harness));
    }

    [Fact]
    public async Task SwapFailureKeepsTheOldIndexAndReportsThatMergeDidNotStartAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        harness.Recorder.OnExecuting = command =>
        {
            if (command.CommandText.Contains(
                "CREATE INDEX \"IX_LogEntries_DuplicateCheck_Md5\"",
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException("swap stopped");
            }
        };

        await harness.Service.RequestRunAsync();
        var run = await WaitForNewRunAsync(harness.Service, previous: null);
        await run;
        Assert.Empty(MergeEvents(harness));
        var failure = Assert.Single(FailedUpgradeRuns(harness));
        Assert.Contains("The merge has not started", failure.Error);

        await using var context = harness.SeedContexts.CreateDbContext();
        var definition = await context.Database.SqlQueryRaw<string>("""
            SELECT pg_get_indexdef(c.oid) AS "Value"
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema()
              AND c.relname = 'IX_LogEntries_DuplicateCheck'
            """).SingleAsync();
        Assert.DoesNotContain("md5(", definition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellingABatchLeavesAConsistentPlanAndTheNextRunFinishesAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await SeedIslandAsync(harness, rows: 3, logsPerRow: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        harness.Recorder.OnExecuting = command =>
        {
            if (command.CommandText.Contains("WITH members AS", StringComparison.Ordinal)
                && Interlocked.Exchange(ref held, 1) == 0)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        };

        await harness.Service.RequestRunAsync();
        var run = await WaitForNewRunAsync(harness.Service, previous: null);
        await entered.Task;
        var operation = Assert.Single(
            harness.Tracker.GetActiveOperations(OperationType.DownloadHistoryUpgrade));
        Assert.Equal(
            OperationCancelResult.Requested,
            harness.Tracker.CancelOperation(operation.Id));
        release.TrySetResult();
        await run;
        Assert.Equal(OperationStatus.Cancelled, harness.Tracker.GetOperation(operation.Id)!.Status);

        await using (var check = harness.Contexts.CreateDbContext())
        {
            var rows = await check.Downloads.CountAsync();
            Assert.Contains(rows, new[] { 1, 3 });
            Assert.Equal(3, await check.LogEntries.CountAsync());
        }

        harness.Recorder.OnExecuting = null;
        await harness.Service.RequestRunAsync();
        var resumed = await WaitForNewRunAsync(harness.Service, run);
        await resumed;
        await using var final = harness.Contexts.CreateDbContext();
        Assert.Single(await final.Downloads.ToListAsync());
        Assert.True(await final.Database
            .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanIsEmpty)
            .SingleAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartialMergePublishesFinalRefreshAndKeepsTerminalStatusAsync(bool cancelled)
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        var ids = await SeedIslandAsync(harness, rows: 3, logsPerRow: 1);
        await using (var context = harness.SeedContexts.CreateDbContext())
        {
            var eventRow = NewEvent();
            context.Events.Add(eventRow);
            await context.SaveChangesAsync();
            context.EventDownloads.Add(NewTag(eventRow.Id, ids[1], autoTagged: false));
            await context.SaveChangesAsync();
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var folds = 0;
        harness.Recorder.OnExecuting = command =>
        {
            foreach (DbParameter parameter in command.Parameters)
            {
                if (parameter.ParameterName == "batchSize")
                {
                    parameter.Value = 1;
                }
            }

            if (!command.CommandText.Contains("WITH members AS", StringComparison.Ordinal)
                || Interlocked.Increment(ref folds) != 2)
            {
                return;
            }

            if (!cancelled)
            {
                throw new InvalidOperationException("later batch stopped");
            }

            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };

        await harness.Service.RequestRunAsync();
        var run = await WaitForNewRunAsync(harness.Service, previous: null);
        Guid? operationId = null;
        if (cancelled)
        {
            await entered.Task;
            var operation = Assert.Single(
                harness.Tracker.GetActiveOperations(OperationType.DownloadHistoryUpgrade));
            operationId = operation.Id;
            Assert.Equal(
                OperationCancelResult.Requested,
                harness.Tracker.CancelOperation(operation.Id));
            release.TrySetResult();
        }
        await run;

        if (cancelled)
        {
            Assert.Equal(
                OperationStatus.Cancelled,
                harness.Tracker.GetOperation(operationId!.Value)!.Status);
        }
        else
        {
            var failure = Assert.Single(FailedUpgradeRuns(harness));
            Assert.Contains("later batch stopped", failure.Error);
        }
        Assert.Equal(
            [SignalREvents.DownloadsRefresh, SignalREvents.DownloadHistoryMergeComplete],
            MergeEvents(harness));

        await using var check = harness.Contexts.CreateDbContext();
        var rows = await check.Downloads.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(6, rows.Sum(row => row.CacheHitBytes));
        Assert.Equal(12, rows.Sum(row => row.CacheMissBytes));
        Assert.Equal(3, await check.LogEntries.CountAsync());
        var tag = await check.EventDownloads.SingleAsync();
        Assert.Equal(ids[0], tag.DownloadId);
        Assert.False(tag.AutoTagged);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportTerminalWithCommittedRowsStartsAReplanAsync(bool cancelled)
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        var markerId = harness.RegisterUpgrade();
        await harness.Service.RunAsync(
            markerId,
            DownloadHistoryUpgradeSql.BatchSize,
            replan: false,
            CancellationToken.None);
        harness.Tracker.CompleteOperation(markerId, success: true);
        var noWorkCheckEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNoWorkCheck = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        harness.Recorder.OnExecuting = command =>
        {
            if (command.CommandText.Contains(
                    "SELECT NOT EXISTS (SELECT 1 FROM \"DownloadSessionMergePlan\")",
                    StringComparison.Ordinal)
                && Interlocked.Exchange(ref held, 1) == 0)
            {
                noWorkCheckEntered.TrySetResult();
                releaseNoWorkCheck.Task.GetAwaiter().GetResult();
            }
        };
        await harness.Service.StartAsync(CancellationToken.None);
        await noWorkCheckEntered.Task;

        await using (var seed = harness.Contexts.CreateDbContext())
        {
            seed.Downloads.AddRange(
                NewDownload("10.0.0.9", "steam", SessionStart, 7, "default", false),
                NewDownload("10.0.0.9", "steam", SessionStart.AddSeconds(2), 7, "default", false));
            await seed.SaveChangesAsync();
        }

        var importId = harness.Tracker.RegisterOperation(
            OperationType.DataImport,
            "Historical import",
            new CancellationTokenSource(),
            new DataImportMetrics { RecordsImported = 2 });
        var terminalObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Tracker.OperationTerminal += operation =>
        {
            if (operation.Id == importId)
            {
                terminalObserved.TrySetResult();
            }
        };
        harness.Tracker.CompleteOperation(
            importId,
            success: false,
            error: cancelled ? null : "Import stopped after committed rows",
            cancelled: cancelled);
        await terminalObserved.Task;
        releaseNoWorkCheck.TrySetResult();

        var replan = await WaitForNewRunAsync(harness.Service, previous: null);
        await replan;
        await using var check = harness.Contexts.CreateDbContext();
        Assert.Single(await check.Downloads.ToListAsync());
        Assert.Equal(1, await check.Downloads.CountAsync(row => row.ClientIp == "10.0.0.9"));
    }

    [Fact]
    public async Task AnUnrelatedTerminalOperationDoesNotStartAnotherRunAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await SeedIslandAsync(harness, rows: 2, logsPerRow: 0);
        await harness.Service.StartAsync(CancellationToken.None);
        var first = await WaitForNewRunAsync(harness.Service, previous: null);
        await first;

        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTerminal(OperationInfo operation)
        {
            if (operation.Type == OperationType.EvictionScan)
            {
                observed.TrySetResult();
            }
        }
        harness.Tracker.OperationTerminal += OnTerminal;
        try
        {
            var scanId = harness.Tracker.RegisterOperation(
                OperationType.EvictionScan,
                "Eviction scan",
                new CancellationTokenSource());
            harness.Tracker.CompleteOperation(scanId, success: true);
            await observed.Task;
        }
        finally
        {
            harness.Tracker.OperationTerminal -= OnTerminal;
        }

        Assert.Same(first, harness.Service.LastRun);
    }

    [Fact]
    public async Task ImportAfterAFailedRunDropsTheLeftoverPlanAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await SeedIslandAsync(harness, rows: 2, logsPerRow: 0);
        harness.Recorder.OnExecuting = command =>
        {
            if (command.CommandText.Contains("WITH members AS", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("boom");
            }
        };
        await harness.Service.StartAsync(CancellationToken.None);
        var failed = await WaitForNewRunAsync(harness.Service, previous: null);
        await failed;

        await using (var seed = harness.SeedContexts.CreateDbContext())
        {
            seed.Downloads.AddRange(
                NewDownload("10.0.0.10", "steam", SessionStart, 8, "default", false),
                NewDownload("10.0.0.10", "steam", SessionStart.AddSeconds(2), 8, "default", false));
            await seed.SaveChangesAsync();
        }
        harness.Recorder.OnExecuting = null;
        var importId = harness.Tracker.RegisterOperation(
            OperationType.DataImport,
            "Historical import",
            new CancellationTokenSource(),
            new DataImportMetrics { RecordsImported = 1 });
        harness.Tracker.CompleteOperation(importId, success: true);

        var resumed = await WaitForNewRunAsync(harness.Service, failed);
        await resumed;
        await using var check = harness.Contexts.CreateDbContext();
        Assert.Equal(2, await check.Downloads.CountAsync());
        Assert.Equal(1, await check.Downloads.CountAsync(row => row.ClientIp == "10.0.0.10"));
        Assert.True(await check.Database
            .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanIsEmpty)
            .SingleAsync());
    }

    [Fact]
    public async Task ResolverChangesDuringTheMergeRemainPartOfTheIslandAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        var ids = await SeedIslandAsync(
            harness,
            rows: 3,
            logsPerRow: 0,
            service: "wsus",
            depotId: null,
            lastActive: true);
        var changed = 0;
        harness.Recorder.OnExecuting = command =>
        {
            if (command.CommandText.StartsWith("LOCK TABLE \"Downloads\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref changed, 1) == 0)
            {
                using var connection = new NpgsqlConnection(harness.ConnectionString);
                connection.Open();
                using var update = new NpgsqlCommand("""
                    UPDATE "Downloads"
                    SET "Service" = 'xbox', "GameName" = 'Game X', "XboxProductId" = 'P1'
                    WHERE "Id" = ANY(@ids)
                    """, connection);
                update.Parameters.AddWithValue("ids", new[] { ids[0], ids[1] });
                update.ExecuteNonQuery();
            }
        };

        var operationId = harness.RegisterUpgrade();
        await harness.Service.RunAsync(operationId, 5000, replan: false, CancellationToken.None);

        await using var context = harness.Contexts.CreateDbContext();
        var download = await context.Downloads.SingleAsync();
        Assert.Equal("xbox", download.Service);
        Assert.Equal("Game X", download.GameName);
        Assert.Equal("P1", download.XboxProductId);
        Assert.True(download.IsActive);
    }

    [Fact]
    public async Task ChangedIdentityIsSkippedAndReplannedOnceAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        var ids = await SeedIslandAsync(harness, rows: 2, logsPerRow: 0);
        var changed = 0;
        harness.Recorder.OnExecuting = command =>
        {
            if (command.CommandText.StartsWith("LOCK TABLE \"Downloads\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref changed, 1) == 0)
            {
                using var connection = new NpgsqlConnection(harness.ConnectionString);
                connection.Open();
                using var update = new NpgsqlCommand(
                    "UPDATE \"Downloads\" SET \"Datasource\" = 'other' WHERE \"Id\" = @id",
                    connection);
                update.Parameters.AddWithValue("id", ids[1]);
                update.ExecuteNonQuery();
            }
        };

        var operationId = harness.RegisterUpgrade();
        await harness.Service.RunAsync(operationId, 5000, replan: false, CancellationToken.None);

        await using var context = harness.Contexts.CreateDbContext();
        Assert.Equal(2, await context.Downloads.CountAsync());
        Assert.True(await context.Database
            .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanIsEmpty)
            .SingleAsync());
        Assert.Equal(
            2,
            harness.Recorder.Commands.Count(command =>
                command.Contains("CREATE TABLE \"DownloadSessionMergePlan\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AResolvedTitleReplacesAPlaceholderDuringTheMergeAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        var ids = await SeedIslandAsync(harness, rows: 2, logsPerRow: 0);
        var changed = 0;
        harness.Recorder.OnExecuting = command =>
        {
            if (command.CommandText.StartsWith("LOCK TABLE \"Downloads\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref changed, 1) == 0)
            {
                using var connection = new NpgsqlConnection(harness.ConnectionString);
                connection.Open();
                using var placeholder = new NpgsqlCommand(
                    "UPDATE \"Downloads\" SET \"GameName\" = 'Steam App 10' WHERE \"Id\" = @id",
                    connection);
                placeholder.Parameters.AddWithValue("id", ids[0]);
                placeholder.ExecuteNonQuery();
                using var resolved = new NpgsqlCommand(
                    "UPDATE \"Downloads\" SET \"GameName\" = 'Portal' WHERE \"Id\" = @id",
                    connection);
                resolved.Parameters.AddWithValue("id", ids[1]);
                resolved.ExecuteNonQuery();
            }
        };

        var operationId = harness.RegisterUpgrade();
        await harness.Service.RunAsync(operationId, 5000, replan: false, CancellationToken.None);

        await using var context = harness.Contexts.CreateDbContext();
        var download = await context.Downloads.SingleAsync();
        Assert.Equal("Portal", download.GameName);
    }

    [Fact]
    public async Task ConflictingResolvedTitlesStaySeparateAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        var ids = await SeedIslandAsync(harness, rows: 3, logsPerRow: 0);
        var changed = 0;
        harness.Recorder.OnExecuting = command =>
        {
            if (command.CommandText.StartsWith("LOCK TABLE \"Downloads\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref changed, 1) == 0)
            {
                using var connection = new NpgsqlConnection(harness.ConnectionString);
                connection.Open();
                using var update = new NpgsqlCommand("""
                    UPDATE "Downloads"
                    SET "GameName" = CASE "Id" WHEN @first THEN 'Game X' WHEN @second THEN 'Game Y' END
                    WHERE "Id" IN (@first, @second)
                    """, connection);
                update.Parameters.AddWithValue("first", ids[1]);
                update.Parameters.AddWithValue("second", ids[2]);
                update.ExecuteNonQuery();
            }
        };

        var operationId = harness.RegisterUpgrade();
        await harness.Service.RunAsync(operationId, 5000, replan: false, CancellationToken.None);

        await using var context = harness.Contexts.CreateDbContext();
        Assert.Equal(3, await context.Downloads.CountAsync());
        Assert.Equal(
            [null, "Game X", "Game Y"],
            await context.Downloads.OrderBy(row => row.Id).Select(row => row.GameName).ToListAsync());
    }

    [Fact]
    public async Task AManualTagDuringTheBatchIsRefusedVisiblyAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        var ids = await SeedIslandAsync(harness, rows: 2, logsPerRow: 0);
        long eventId;
        await using (var context = harness.Contexts.CreateDbContext())
        {
            var eventRow = NewEvent();
            context.Events.Add(eventRow);
            await context.SaveChangesAsync();
            eventId = eventRow.Id;
        }

        Task? tagInsert = null;
        var started = 0;
        harness.Recorder.OnExecuting = command =>
        {
            if (command.CommandText.Contains("WITH members AS", StringComparison.Ordinal)
                && Interlocked.Exchange(ref started, 1) == 0)
            {
                tagInsert = Task.Run(async () =>
                {
                    await using var connection = new NpgsqlConnection(harness.ConnectionString);
                    await connection.OpenAsync();
                    await using var insert = new NpgsqlCommand("""
                        INSERT INTO "EventDownloads" ("EventId", "DownloadId", "TaggedAtUtc", "AutoTagged")
                        VALUES (@eventId, @downloadId, now(), false)
                        """, connection);
                    insert.Parameters.AddWithValue("eventId", eventId);
                    insert.Parameters.AddWithValue("downloadId", ids[1]);
                    await insert.ExecuteNonQueryAsync();
                });
                WaitForWaitingLockAsync(harness.ConnectionString, "EventDownloads")
                    .GetAwaiter()
                    .GetResult();
            }
        };

        var operationId = harness.RegisterUpgrade();
        await harness.Service.RunAsync(operationId, 5000, replan: false, CancellationToken.None);

        Assert.NotNull(tagInsert);
        var exception = await Assert.ThrowsAsync<PostgresException>(async () => await tagInsert!);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        await using var check = harness.Contexts.CreateDbContext();
        Assert.Empty(await check.EventDownloads.ToListAsync());
        Assert.Single(await check.Downloads.ToListAsync());
    }

    [Fact]
    public async Task AStartedIngestTransactionHoldsTheBatchUntilItCommitsAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await SeedIslandAsync(harness, rows: 2, logsPerRow: 0);
        await CreatePlanAsync(harness);

        await using var blocker = new NpgsqlConnection(harness.ConnectionString);
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "LOCK TABLE \"Downloads\" IN ROW EXCLUSIVE MODE",
            blocker,
            blockerTransaction))
        {
            await lockCommand.ExecuteNonQueryAsync();
        }

        var operationId = harness.RegisterUpgrade();
        var run = harness.Service.RunAsync(
            operationId,
            5000,
            replan: false,
            CancellationToken.None);
        await WaitForWaitingLockAsync(harness.ConnectionString, "Downloads");
        Assert.False(run.IsCompleted);

        await blockerTransaction.CommitAsync();
        await run;
        await using var check = harness.Contexts.CreateDbContext();
        Assert.Single(await check.Downloads.ToListAsync());
    }

    [Fact]
    public async Task CleanupHoldingDownloadsMakesTheUpgradeWaitBeforeLaterLocksAsync()
    {
        var gate = new CleanupSelectGate();
        await using var harness = await UpgradeHarness.CreateAsync(gate);
        var ids = await SeedIslandAsync(harness, rows: 2, logsPerRow: 1);
        await using (var seed = harness.SeedContexts.CreateDbContext())
        {
            await seed.Downloads.ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsActive, true));
            var eventRow = NewEvent();
            seed.Events.Add(eventRow);
            await seed.SaveChangesAsync();
            seed.EventDownloads.Add(NewTag(eventRow.Id, ids[1], autoTagged: false));
            await seed.SaveChangesAsync();
        }
        await SwapIndexAsync(harness.ConnectionString);
        await CreatePlanAsync(harness);
        harness.Recorder.Clear();
        harness.NotificationRecorder.Invocations.Clear();

        await using var cleanupContext = harness.Contexts.CreateDbContext();
        var cleanupTask = DownloadCleanupService.CleanupBatchAsync(
            cleanupContext,
            DateTime.UtcNow.AddSeconds(-15),
            DateTime.UtcNow.AddSeconds(-60),
            10,
            CancellationToken.None);
        Task<int> upgradeTask = Task.FromResult(0);
        var cleanupResult = 0;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var operationId = harness.RegisterUpgrade();
            upgradeTask = harness.Service.RunAsync(
                operationId,
                5000,
                replan: false,
                CancellationToken.None);
            await WaitForWaitingLockAsync(harness.ConnectionString, "Downloads");
            Assert.False(upgradeTask.IsCompleted);
            Assert.DoesNotContain(
                harness.Recorder.Commands,
                command => command.StartsWith(
                    "LOCK TABLE \"LogEntries\"",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                harness.Recorder.Commands,
                command => command.StartsWith(
                    "LOCK TABLE \"EventDownloads\"",
                    StringComparison.Ordinal));
        }
        finally
        {
            gate.Release.TrySetResult();
            cleanupResult = await cleanupTask;
            await upgradeTask;
        }

        Assert.Equal(2, cleanupResult);
        await using var check = harness.Contexts.CreateDbContext();
        var combined = await check.Downloads.SingleAsync();
        Assert.Equal(ids[0], combined.Id);
        Assert.False(combined.IsActive);
        Assert.Equal(3, combined.CacheHitBytes);
        Assert.Equal(6, combined.CacheMissBytes);
        Assert.Equal(2, await check.LogEntries.CountAsync());
        Assert.All(await check.LogEntries.ToListAsync(), entry => Assert.Equal(combined.Id, entry.DownloadId));
        var tag = await check.EventDownloads.SingleAsync();
        Assert.Equal(combined.Id, tag.DownloadId);
        Assert.False(tag.AutoTagged);
        Assert.Equal(
            [SignalREvents.DownloadsRefresh, SignalREvents.DownloadHistoryMergeComplete],
            MergeEvents(harness));
    }

    [Fact]
    public async Task UpgradeHoldingMergeLocksMakesCleanupSelectTheMergedSurvivorAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        var ids = await SeedIslandAsync(harness, rows: 2, logsPerRow: 1);
        await using (var seed = harness.SeedContexts.CreateDbContext())
        {
            await seed.Downloads.ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.IsActive, true));
        }
        await SwapIndexAsync(harness.ConnectionString);
        await CreatePlanAsync(harness);
        harness.Recorder.Clear();
        harness.NotificationRecorder.Invocations.Clear();

        var mergeEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMerge = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        harness.Recorder.OnExecuting = command =>
        {
            if (command.CommandText.Contains("WITH members AS", StringComparison.Ordinal)
                && Interlocked.Exchange(ref held, 1) == 0)
            {
                mergeEntered.TrySetResult();
                releaseMerge.Task.GetAwaiter().GetResult();
            }
        };

        var operationId = harness.RegisterUpgrade();
        var upgradeTask = harness.Service.RunAsync(
            operationId,
            5000,
            replan: false,
            CancellationToken.None);
        await mergeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var cleanupContext = harness.Contexts.CreateDbContext();
        await cleanupContext.Database.OpenConnectionAsync();
        var cleanupPid = ((NpgsqlConnection)cleanupContext.Database.GetDbConnection()).ProcessID;
        var cleanupTask = DownloadCleanupService.CleanupBatchAsync(
            cleanupContext,
            DateTime.UtcNow.AddSeconds(-15),
            DateTime.UtcNow.AddSeconds(-60),
            10,
            CancellationToken.None);
        var cleanupResult = 0;
        try
        {
            await WaitForWaitingLockAsync(harness.ConnectionString, "Downloads", cleanupPid);
            Assert.DoesNotContain(harness.Recorder.Commands, IsCleanupSelect);
        }
        finally
        {
            releaseMerge.TrySetResult();
            await upgradeTask;
            cleanupResult = await cleanupTask;
        }

        Assert.Equal(1, cleanupResult);
        await using var check = harness.Contexts.CreateDbContext();
        var combined = await check.Downloads.SingleAsync();
        Assert.Equal(ids[0], combined.Id);
        Assert.False(combined.IsActive);
        Assert.Equal(3, combined.CacheHitBytes);
        Assert.Equal(6, combined.CacheMissBytes);
        Assert.Equal(2, await check.LogEntries.CountAsync());
        Assert.All(await check.LogEntries.ToListAsync(), entry => Assert.Equal(combined.Id, entry.DownloadId));
        Assert.Equal(
            [SignalREvents.DownloadsRefresh, SignalREvents.DownloadHistoryMergeComplete],
            MergeEvents(harness));
    }

    [Fact]
    public async Task AnOrphanedIndexBuildIsStoppedAndTheSwapCompletesAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await using (var writer = new NpgsqlConnection(harness.ConnectionString))
        {
            await writer.OpenAsync();
            await using var writerTransaction = await writer.BeginTransactionAsync();
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO "LogEntries"
                    ("Timestamp", "ClientIp", "Service", "Method", "Url", "StatusCode",
                     "BytesServed", "CacheStatus", "Datasource", "CreatedAt")
                VALUES (now(), '10.0.0.2', 'steam', 'GET', '/held', 200, 1, 'HIT', 'default', now())
                """, writer, writerTransaction))
            {
                await insert.ExecuteNonQueryAsync();
            }

            var blockedSwap = SwapIndexAsync(harness.ConnectionString);
            await WaitForWaitingLockAsync(harness.ConnectionString, "LogEntries");
            Assert.False(blockedSwap.IsCompleted);
            await writerTransaction.CommitAsync();
            await blockedSwap;
        }

        await using (var reset = new NpgsqlConnection(harness.ConnectionString))
        {
            await reset.OpenAsync();
            await using var command = new NpgsqlCommand("""
                DROP INDEX "IX_LogEntries_DuplicateCheck";
                CREATE INDEX "IX_LogEntries_DuplicateCheck" ON "LogEntries"
                    ("ClientIp", "Service", "Timestamp", "Url", "BytesServed", "Datasource");
                """, reset);
            await command.ExecuteNonQueryAsync();
        }

        await using (var seed = new NpgsqlConnection(harness.ConnectionString))
        {
            await seed.OpenAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO "LogEntries"
                    ("Timestamp", "ClientIp", "Service", "Method", "Url", "StatusCode",
                     "BytesServed", "CacheStatus", "HttpRange", "Datasource", "CreatedAt")
                SELECT now(), '10.0.0.1', 'steam', 'GET', '/cache/' || value::text, 200,
                       value, 'HIT', 'bytes=' || value::text || '-', 'default', now()
                FROM generate_series(1, 2000) AS value;
                CREATE FUNCTION "SlowIndexKey"(value text) RETURNS text
                LANGUAGE plpgsql IMMUTABLE PARALLEL UNSAFE
                AS $function$
                BEGIN
                    PERFORM pg_sleep(0.002);
                    RETURN md5(value);
                END;
                $function$;
                """, seed);
            await command.ExecuteNonQueryAsync();
        }

        await using var connection = new NpgsqlConnection(harness.ConnectionString);
        await connection.OpenAsync();
        await using var build = new NpgsqlCommand(
            "CREATE INDEX \"IX_LogEntries_DuplicateCheck_Md5\" ON \"LogEntries\" "
                + "(\"ClientIp\", \"Service\", \"Timestamp\", \"Url\", \"BytesServed\", "
                + "\"Datasource\", md5(COALESCE(\"HttpRange\", '')), \"SlowIndexKey\"(\"Url\"))",
            connection);
        var buildTask = build.ExecuteNonQueryAsync();
        await WaitForIndexBuildAsync(harness.ConnectionString);

        var operationId = harness.RegisterUpgrade();
        var run = harness.Service.RunAsync(
            operationId,
            5000,
            replan: false,
            CancellationToken.None);
        var exception = await Assert.ThrowsAsync<PostgresException>(async () => await buildTask);
        Assert.Equal(PostgresErrorCodes.AdminShutdown, exception.SqlState);
        await run;

        await using var context = harness.Contexts.CreateDbContext();
        var indexState = await context.Database
            .SqlQueryRaw<string>(DownloadHistoryUpgradeSql.DuplicateCheckIndexState)
            .SingleAsync();
        Assert.Equal("ready", indexState);
    }

    [Fact]
    public async Task ProgressMovesFromIndexingThroughMergingToCompletionAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await SeedIslandAsync(harness, rows: 5, logsPerRow: 0);
        await harness.Service.StartAsync(CancellationToken.None);
        var run = await WaitForNewRunAsync(harness.Service, previous: null);
        await run;

        var rows = harness.NotificationRecorder.Invocations
            .Where(call => call.Method == nameof(ISignalRNotificationService.NotifyAdminAsync)
                && (string?)call.Args[0] == SignalREvents.OperationUpdated
                && call.Args[1] is OperationRun operation
                && operation.OperationType == OperationType.DownloadHistoryUpgrade.ToWireString())
            .Select(call => (OperationRun)call.Args[1]!)
            .ToList();
        Assert.Contains(rows, row => row.Message == DownloadHistoryUpgradeService.IndexingStageKey
            && row.PercentComplete == 0);
        Assert.Contains(rows, row => row.Message == DownloadHistoryUpgradeService.MergingStageKey
            && row.PercentComplete >= 5);
        Assert.Contains(rows, row => row.Status == "completed" && row.PercentComplete == 100);
    }

    [Fact]
    public async Task CancellingWhileWaitingResetsTheRequestFlagAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await SeedIslandAsync(harness, rows: 2, logsPerRow: 0);
        var blockerId = harness.Tracker.RegisterOperation(
            OperationType.LogProcessing,
            "Log processing",
            new CancellationTokenSource());
        await harness.Service.StartAsync(CancellationToken.None);
        var waiting = await WaitForWaitingUpgradeAsync(harness.Tracker);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTerminal(OperationInfo operation)
        {
            if (operation.Id == waiting.Id)
            {
                cancelled.TrySetResult();
            }
        }
        harness.Tracker.OperationTerminal += OnTerminal;
        try
        {
            Assert.Equal(OperationCancelResult.Requested, harness.Tracker.CancelOperation(waiting.Id));
            await cancelled.Task;
        }
        finally
        {
            harness.Tracker.OperationTerminal -= OnTerminal;
        }

        harness.Tracker.CompleteOperation(blockerId, success: true);
        Assert.Null(harness.Service.LastRun);
        await harness.Service.RequestRunAsync();
        var run = await WaitForNewRunAsync(harness.Service, previous: null);
        await run;
        await using var check = harness.Contexts.CreateDbContext();
        Assert.Single(await check.Downloads.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitInterruptionResumesWithoutDoubleCountingAsync(bool after)
    {
        await using var harness = await UpgradeHarness.CreateAsync(new ResetCommitFault(2, after));
        var ids = await SeedIslandAsync(harness, rows: 3, logsPerRow: 1);
        await using (var context = harness.SeedContexts.CreateDbContext())
        {
            var eventRow = NewEvent();
            context.Events.Add(eventRow);
            await context.SaveChangesAsync();
            context.EventDownloads.Add(NewTag(eventRow.Id, ids[1], autoTagged: false));
            await context.SaveChangesAsync();
        }
        await SwapIndexAsync(harness.ConnectionString);

        var failedId = harness.RegisterUpgrade();
        await Assert.ThrowsAsync<IOException>(() => harness.Service.RunAsync(
            failedId,
            batchSize: 1,
            replan: false,
            CancellationToken.None));
        Assert.Equal(
            [SignalREvents.DownloadsRefresh, SignalREvents.DownloadHistoryMergeComplete],
            MergeEvents(harness));

        await using (var interrupted = harness.Contexts.CreateDbContext())
        {
            Assert.Equal(after ? 2 : 3, await interrupted.Downloads.CountAsync());
            Assert.Equal(3, await interrupted.LogEntries.CountAsync());
            Assert.Single(await interrupted.EventDownloads.ToListAsync());
        }

        harness.NotificationRecorder.Invocations.Clear();
        var resumedId = harness.RegisterUpgrade();
        await harness.Service.RunAsync(
            resumedId,
            batchSize: 1,
            replan: false,
            CancellationToken.None);
        await using var final = harness.Contexts.CreateDbContext();
        var download = await final.Downloads.SingleAsync();
        Assert.Equal(6, download.CacheHitBytes);
        Assert.Equal(12, download.CacheMissBytes);
        Assert.Equal(3, await final.LogEntries.CountAsync());
        var tag = await final.EventDownloads.SingleAsync();
        Assert.Equal(download.Id, tag.DownloadId);
        Assert.False(tag.AutoTagged);
        Assert.Equal(
            [SignalREvents.DownloadsRefresh, SignalREvents.DownloadHistoryMergeComplete],
            MergeEvents(harness));
    }

    [Fact]
    public async Task MeasuresAFullBatchAndTheIndexSwapAsync()
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await using (var connection = new NpgsqlConnection(harness.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var downloads = new NpgsqlCommand("""
                INSERT INTO "Downloads"
                    ("Service", "ClientIp", "StartTimeUtc", "EndTimeUtc", "CacheHitBytes",
                     "CacheMissBytes", "IsActive", "IsEvicted", "DepotId", "Datasource", "LastUrl")
                SELECT 'steam', '10.0.0.20',
                       timestamptz '2026-09-29 12:00:00+00' + value * interval '2 seconds',
                       timestamptz '2026-09-29 12:00:00+00' + value * interval '2 seconds' + interval '3 seconds',
                       1, 2, false, false, 1, 'default', '/measure/' || value::text
                FROM generate_series(0, 5000) AS value
                """, connection))
            {
                await downloads.ExecuteNonQueryAsync();
            }
            await using (var logs = new NpgsqlCommand("""
                INSERT INTO "LogEntries"
                    ("Timestamp", "ClientIp", "Service", "Method", "Url", "StatusCode",
                     "BytesServed", "CacheStatus", "HttpRange", "Datasource", "DownloadId", "CreatedAt")
                SELECT d."StartTimeUtc", d."ClientIp", d."Service", 'GET',
                       '/measure/log/' || d."Id"::text || '/' || item::text,
                       200, 1, 'HIT', 'bytes=' || item::text || '-', d."Datasource", d."Id", d."StartTimeUtc"
                FROM "Downloads" d
                CROSS JOIN generate_series(1, 10) AS item
                WHERE d."ClientIp" = '10.0.0.20'
                """, connection))
            {
                await logs.ExecuteNonQueryAsync();
            }
        }

        var indexTimer = Stopwatch.StartNew();
        await SwapIndexAsync(harness.ConnectionString);
        indexTimer.Stop();
        await CreatePlanAsync(harness);

        var operationId = harness.RegisterUpgrade();
        var batchTimer = Stopwatch.StartNew();
        var batches = await harness.Service.RunAsync(
            operationId,
            DownloadHistoryUpgradeSql.BatchSize,
            replan: false,
            CancellationToken.None);
        batchTimer.Stop();

        Console.WriteLine(
            "Download history measurement: index swap {0} ms; one {1}-row batch with 10 LogEntries per absorbed row {2} ms",
            indexTimer.ElapsedMilliseconds,
            DownloadHistoryUpgradeSql.BatchSize,
            batchTimer.ElapsedMilliseconds);
        Assert.Equal(1, batches);
        await using var check = harness.Contexts.CreateDbContext();
        Assert.Single(await check.Downloads.Where(row => row.ClientIp == "10.0.0.20").ToListAsync());
        Assert.Equal(
            50010,
            await check.LogEntries.CountAsync(row => row.ClientIp == "10.0.0.20"));
    }

    private static List<OperationRun> FailedUpgradeRuns(UpgradeHarness harness) =>
        harness.Tracker.GetRuns().Runs
            .Where(run => run.OperationType == OperationType.DownloadHistoryUpgrade.ToWireString()
                && run.Status == "failed"
                && !run.Closed)
            .ToList();

    private static List<string> MergeEvents(UpgradeHarness harness) =>
        harness.NotificationRecorder.Invocations
            .Where(call => call.Method == nameof(ISignalRNotificationService.NotifyAllAsync)
                && call.Args[0] is SignalREvents.DownloadsRefresh
                    or SignalREvents.DownloadHistoryMergeComplete)
            .Select(call => (string)call.Args[0]!)
            .ToList();

    private static async Task CreatePlanAsync(UpgradeHarness harness)
    {
        await using var context = harness.Contexts.CreateDbContext();
        await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            await context.Database.ExecuteSqlRawAsync(DownloadHistoryUpgradeSql.CreatePlan);
            await transaction.CommitAsync();
        });
    }

    private static async Task WaitForWaitingLockAsync(
        string connectionString,
        string relation,
        int? processId = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks l
                    JOIN pg_class c ON c.oid = l.relation
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = current_schema()
                      AND c.relname = @relation
                      AND (@processId IS NULL OR l.pid = @processId)
                      AND NOT l.granted)
                """, connection);
            command.Parameters.AddWithValue("relation", relation);
            command.Parameters.Add(new NpgsqlParameter("processId", NpgsqlDbType.Integer)
            {
                Value = (object?)processId ?? DBNull.Value
            });
            if ((bool)(await command.ExecuteScalarAsync())!)
            {
                return;
            }
            await Task.Delay(5);
        }

        throw new TimeoutException($"No blocked lock appeared for {relation}");
    }

    private static bool IsCleanupSelect(string command) =>
        command.StartsWith("SELECT d.\"Id\"", StringComparison.Ordinal)
        && command.Contains("d.\"IsActive\"", StringComparison.Ordinal)
        && command.Contains("LIMIT", StringComparison.Ordinal);

    private static async Task WaitForIndexBuildAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_progress_create_index
                    WHERE relid = to_regclass('"LogEntries"'))
                """, connection);
            if ((bool)(await command.ExecuteScalarAsync())!)
            {
                return;
            }
            await Task.Delay(5);
        }

        throw new TimeoutException("The orphaned index build did not enter PostgreSQL progress tracking");
    }

    private static async Task<OperationInfo> WaitForWaitingUpgradeAsync(
        UnifiedOperationTracker tracker)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var waiting = tracker.GetWaitingOperations()
                .SingleOrDefault(operation => operation.Type == OperationType.DownloadHistoryUpgrade);
            if (waiting is not null)
            {
                return waiting;
            }
            await Task.Delay(10);
        }

        throw new TimeoutException("The download history upgrade did not enter the operation queue");
    }

    private static async Task SwapIndexAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var statement in new[]
        {
            DownloadHistoryUpgradeSql.DropMd5IndexIfExists,
            DownloadHistoryUpgradeSql.CreateMd5Index,
            DownloadHistoryUpgradeSql.DropOldIndexIfExists,
            DownloadHistoryUpgradeSql.RenameMd5Index
        })
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task<(long Hit, long Miss, int Logs, string? LastUrl)> FoldSnapshotAsync(
        int batchSize)
    {
        await using var harness = await UpgradeHarness.CreateAsync();
        await SeedIslandAsync(harness, rows: 5, logsPerRow: 2);
        var operationId = harness.RegisterUpgrade();
        await harness.Service.RunAsync(operationId, batchSize, replan: false, CancellationToken.None);
        await using var context = harness.Contexts.CreateDbContext();
        var download = await context.Downloads.SingleAsync();
        return (
            download.CacheHitBytes,
            download.CacheMissBytes,
            await context.LogEntries.CountAsync(),
            download.LastUrl);
    }

    private static async Task<List<long>> SeedIslandAsync(
        UpgradeHarness harness,
        int rows,
        int logsPerRow,
        string service = "steam",
        long? depotId = 1,
        bool lastActive = false)
    {
        await using var context = harness.SeedContexts.CreateDbContext();
        var downloads = Enumerable.Range(0, rows)
            .Select(index =>
            {
                var row = NewDownload(
                    "10.0.0.1",
                    service,
                    SessionStart.AddSeconds(index * 2),
                    depotId,
                    "default",
                    false,
                    $"/chunk/{index}");
                row.CacheHitBytes = index + 1;
                row.CacheMissBytes = (index + 1) * 2;
                row.IsActive = lastActive && index == rows - 1;
                return row;
            })
            .ToList();
        context.Downloads.AddRange(downloads);
        await context.SaveChangesAsync();

        foreach (var download in downloads)
        {
            for (var index = 0; index < logsPerRow; index++)
            {
                context.LogEntries.Add(NewLogEntry(download.Id, $"/log/{download.Id}/{index}", null));
            }
        }
        await context.SaveChangesAsync();
        return downloads.Select(row => row.Id).ToList();
    }

    private static Download NewDownload(
        string clientIp,
        string service,
        DateTime start,
        long? depotId,
        string datasource,
        bool evicted,
        string? lastUrl = null) => new()
        {
            ClientIp = clientIp,
            Service = service,
            StartTimeUtc = start,
            EndTimeUtc = start.AddSeconds(1),
            CacheHitBytes = 1,
            CacheMissBytes = 2,
            IsActive = false,
            IsEvicted = evicted,
            DepotId = depotId,
            Datasource = datasource,
            LastUrl = lastUrl
        };

    private static LogEntryRecord NewLogEntry(long? downloadId, string url, string? range) => new()
    {
        Timestamp = SessionStart,
        ClientIp = "10.0.0.1",
        Service = "steam",
        Method = "GET",
        Url = url,
        StatusCode = 200,
        BytesServed = 1024,
        CacheStatus = "HIT",
        HttpRange = range,
        Datasource = "default",
        DownloadId = downloadId,
        CreatedAt = SessionStart
    };

    private static Event NewEvent() => new()
    {
        Name = "LAN Party",
        StartTimeUtc = SessionStart.AddHours(-1),
        EndTimeUtc = SessionStart.AddHours(2),
        ColorIndex = 1,
        CreatedAtUtc = SessionStart.AddHours(-1)
    };

    private static EventDownload NewTag(long eventId, long downloadId, bool autoTagged) => new()
    {
        EventId = eventId,
        DownloadId = downloadId,
        TaggedAtUtc = SessionStart,
        AutoTagged = autoTagged
    };

    private static string CreateLongValue(int seed)
    {
        var random = new Random(seed);
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Range(0, 2000)
            .Select(_ => alphabet[random.Next(alphabet.Length)])
            .ToArray());
    }

    private static async Task AssertRawSevenColumnIndexRejectsLongValuesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("""
            CREATE TEMP TABLE "RawIndexControl" (
                "ClientIp" text NOT NULL,
                "Service" text NOT NULL,
                "Timestamp" timestamptz NOT NULL,
                "Url" text NOT NULL,
                "BytesServed" bigint NOT NULL,
                "Datasource" text NOT NULL,
                "HttpRange" text
            );
            CREATE INDEX "IX_RawIndexControl" ON "RawIndexControl"
                ("ClientIp", "Service", "Timestamp", "Url", "BytesServed", "Datasource", "HttpRange");
            """, connection))
        {
            await setup.ExecuteNonQueryAsync();
        }

        await using var insert = new NpgsqlCommand("""
            INSERT INTO "RawIndexControl"
                ("ClientIp", "Service", "Timestamp", "Url", "BytesServed", "Datasource", "HttpRange")
            VALUES ('10.0.0.1', 'steam', now(), @url, 1, 'default', @range)
            """, connection);
        insert.Parameters.AddWithValue("url", CreateLongValue(41));
        insert.Parameters.AddWithValue("range", CreateLongValue(43));
        var exception = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ProgramLimitExceeded, exception.SqlState);
    }

    private static async Task MarkUpgradeCompleteAsync(UpgradeHarness harness)
    {
        var operationId = harness.RegisterUpgrade();
        await harness.Service.RunAsync(
            operationId,
            DownloadHistoryUpgradeSql.BatchSize,
            replan: false,
            CancellationToken.None);
        harness.Tracker.CompleteOperation(operationId, success: true);
    }

    private static async Task<MigrationImportResponse> ImportRowsAsync(
        UpgradeHarness harness,
        IReadOnlyCollection<Download> rows,
        int batchSize = 1000,
        bool overwriteExisting = false,
        Func<Task>? batchCommitted = null,
        CancellationToken cancellationToken = default)
    {
        await using (var context = harness.Contexts.CreateDbContext())
        {
            // Migrated databases retain this default, which the generated test schema omits.
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Downloads\" ALTER COLUMN \"IsEvicted\" SET DEFAULT FALSE");
        }
        var sourceConnection = await CreateSourceAsync(harness.ConnectionString, rows);
        try
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = harness.ConnectionString
            };
            var controller = new DataMigrationController(
                NullLogger<DataMigrationController>.Instance,
                (ISignalRNotificationService)(object)harness.NotificationRecorder,
                harness.Tracker,
                harness.Checker,
                new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            {
                BatchCommitted = batchCommitted
            };
            var action = await controller.ImportLancacheManagerAsync(
                new DataMigrationImportRequest
                {
                    ConnectionString = sourceConnection,
                    BatchSize = batchSize,
                    OverwriteExisting = overwriteExisting
                },
                cancellationToken);
            var ok = Assert.IsType<OkObjectResult>(action);
            return Assert.IsType<MigrationImportResponse>(ok.Value);
        }
        finally
        {
            await DropSourceAsync(sourceConnection);
        }
    }

    private static async Task<string> CreateSourceAsync(
        string targetConnection,
        IReadOnlyCollection<Download> rows)
    {
        var name = $"import_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(targetConnection)
        {
            Database = "postgres",
            SearchPath = string.Empty,
            Pooling = false
        };
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var source = new NpgsqlConnectionStringBuilder(targetConnection)
        {
            Database = name,
            SearchPath = "public",
            Pooling = false
        };
        await using var sourceConnection = new NpgsqlConnection(source.ConnectionString);
        await sourceConnection.OpenAsync();
        await using (var create = new NpgsqlCommand("""
            CREATE TABLE public."Downloads" (
                "Service" text NOT NULL,
                "ClientIp" text NOT NULL,
                "StartTimeUtc" timestamptz NOT NULL,
                "EndTimeUtc" timestamptz NOT NULL,
                "CacheHitBytes" bigint NOT NULL,
                "CacheMissBytes" bigint NOT NULL,
                "IsActive" boolean,
                "DepotId" bigint,
                "GameAppId" bigint,
                "Datasource" text
            )
            """, sourceConnection))
        {
            await create.ExecuteNonQueryAsync();
        }

        foreach (var row in rows)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO public."Downloads" (
                    "Service", "ClientIp", "StartTimeUtc", "EndTimeUtc",
                    "CacheHitBytes", "CacheMissBytes", "IsActive", "DepotId", "GameAppId", "Datasource")
                VALUES (
                    @service, @clientIp, @startTimeUtc, @endTimeUtc,
                    @cacheHitBytes, @cacheMissBytes, @isActive, @depotId, @gameAppId, @datasource)
                """, sourceConnection);
            insert.Parameters.AddWithValue("service", row.Service);
            insert.Parameters.AddWithValue("clientIp", row.ClientIp);
            insert.Parameters.AddWithValue("startTimeUtc", row.StartTimeUtc);
            insert.Parameters.AddWithValue("endTimeUtc", row.EndTimeUtc);
            insert.Parameters.AddWithValue("cacheHitBytes", row.CacheHitBytes);
            insert.Parameters.AddWithValue("cacheMissBytes", row.CacheMissBytes);
            insert.Parameters.AddWithValue("isActive", row.IsActive);
            insert.Parameters.Add(new NpgsqlParameter("depotId", NpgsqlDbType.Bigint)
            {
                Value = (object?)row.DepotId ?? DBNull.Value
            });
            insert.Parameters.Add(new NpgsqlParameter("gameAppId", NpgsqlDbType.Bigint)
            {
                Value = (object?)row.GameAppId ?? DBNull.Value
            });
            insert.Parameters.AddWithValue("datasource", row.Datasource);
            await insert.ExecuteNonQueryAsync();
        }

        return source.ConnectionString;
    }

    private static async Task DropSourceAsync(string sourceConnection)
    {
        var source = new NpgsqlConnectionStringBuilder(sourceConnection);
        var name = source.Database;
        source.Database = "postgres";
        source.SearchPath = string.Empty;
        source.Pooling = false;
        await using var connection = new NpgsqlConnection(source.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static Task<long> CoveredRowsAsync(
        NpgsqlConnection connection,
        string clientIp,
        string service,
        long? depotId,
        long? gameAppId,
        string datasource,
        DateTime start,
        DateTime end,
        long existingMaxId) => CoveredRowsAsync(
            connection,
            DataMigrationController.ImportCoveredRowSql,
            clientIp,
            service,
            depotId,
            gameAppId,
            datasource,
            start,
            end,
            existingMaxId);

    private static async Task<long> CoveredRowsAsync(
        NpgsqlConnection connection,
        string commandText,
        string clientIp,
        string service,
        long? depotId,
        long? gameAppId,
        string datasource,
        DateTime start,
        DateTime end,
        long existingMaxId)
    {
        await using var command = new NpgsqlCommand(commandText, connection);
        command.Parameters.AddWithValue("clientIp", clientIp);
        command.Parameters.AddWithValue("service", service);
        command.Parameters.AddWithValue("datasource", datasource);
        command.Parameters.AddWithValue("startTimeUtc", start);
        command.Parameters.AddWithValue("endTimeUtc", end);
        if (commandText.Contains("@existingMaxId", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("existingMaxId", existingMaxId);
        }
        command.Parameters.Add(new NpgsqlParameter("depotId", NpgsqlDbType.Bigint)
        {
            Value = (object?)depotId ?? DBNull.Value
        });
        if (commandText.Contains("@gameAppId", StringComparison.Ordinal))
        {
            command.Parameters.Add(new NpgsqlParameter("gameAppId", NpgsqlDbType.Bigint)
            {
                Value = (object?)gameAppId ?? DBNull.Value
            });
        }
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<Task> WaitForNewRunAsync(
        DownloadHistoryUpgradeService service,
        Task? previous)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var run = service.LastRun;
            if (run is not null && run != previous)
            {
                return run;
            }
            await Task.Delay(10);
        }

        throw new TimeoutException("The download history upgrade did not start");
    }

    private sealed class CleanupSelectGate : DbCommandInterceptor
    {
        private int _held;

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData evt,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (IsCleanupSelect(command.CommandText)
                && Interlocked.Exchange(ref _held, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return await base.ReaderExecutedAsync(
                command,
                evt,
                result,
                cancellationToken);
        }
    }

    private sealed class UpgradeHarness : IAsyncDisposable
    {
        private UpgradeHarness(
            TestDatabase database,
            IDbContextFactory<AppDbContext> contexts,
            string connectionString,
            RecordingCommandInterceptor recorder,
            RecordingNotificationProxy notificationRecorder,
            UnifiedOperationTracker tracker,
            OperationConflictChecker checker,
            OperationQueueService queue,
            DownloadHistoryUpgradeService service)
        {
            Database = database;
            Contexts = contexts;
            ConnectionString = connectionString;
            Recorder = recorder;
            NotificationRecorder = notificationRecorder;
            Tracker = tracker;
            Checker = checker;
            Queue = queue;
            Service = service;
        }

        private TestDatabase Database { get; }

        public IDbContextFactory<AppDbContext> Contexts { get; }

        public TestDbContextFactory SeedContexts => Database.Factory;

        public string ConnectionString { get; }

        public RecordingCommandInterceptor Recorder { get; }

        public RecordingNotificationProxy NotificationRecorder { get; }

        public UnifiedOperationTracker Tracker { get; }

        public OperationConflictChecker Checker { get; }

        public OperationQueueService Queue { get; }

        public DownloadHistoryUpgradeService Service { get; }

        public TaskCompletionSource StartupCleanup { get; private init; } = null!;

        public static async Task<UpgradeHarness> CreateAsync(params IInterceptor[] interceptors)
        {
            var startupCleanup = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            startupCleanup.TrySetResult();
            return await CreateAsync(startupCleanup, interceptors);
        }

        public static async Task<UpgradeHarness> CreateAsync(
            TaskCompletionSource startupCleanup,
            params IInterceptor[] interceptors)
            => await CreateAsync(
                startupCleanup,
                createContexts: null,
                interceptors: interceptors);

        public static async Task<UpgradeHarness> CreateAsync(
            TaskCompletionSource startupCleanup,
            Func<DbContextOptions<AppDbContext>, IDbContextFactory<AppDbContext>>? createContexts,
            params IInterceptor[] interceptors)
        {
            var database = await TestDatabase.CreateAsync();
            await using var context = database.Factory.CreateDbContext();
            var connectionString = context.Database.GetConnectionString()!;
            var recorder = new RecordingCommandInterceptor();
            var allInterceptors = new List<IInterceptor> { recorder };
            allInterceptors.AddRange(interceptors);
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    connectionString,
                    settings => settings.EnableRetryOnFailure(3, TimeSpan.Zero, null))
                .AddInterceptors(allInterceptors)
                .Options;
            IDbContextFactory<AppDbContext> contexts;
            if (createContexts is null)
            {
                contexts = new TestDbContextFactory(options);
            }
            else
            {
                contexts = createContexts(options);
            }
            var notifications = DispatchProxy
                .Create<ISignalRNotificationService, RecordingNotificationProxy>();
            var notificationRecorder = (RecordingNotificationProxy)(object)notifications;
            var tracker = new UnifiedOperationTracker(
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<UnifiedOperationTracker>.Instance,
                notifications);
            var checker = OperationConflictTestServices.Create(
                tracker,
                NullLogger<OperationConflictChecker>.Instance);
            var queue = new OperationQueueService(
                tracker,
                checker,
                NullLogger<OperationQueueService>.Instance);
            var service = new DownloadHistoryUpgradeService(
                contexts,
                notifications,
                tracker,
                queue,
                startupCleanup.Task,
                NullLogger<DownloadHistoryUpgradeService>.Instance);
            return new UpgradeHarness(
                database,
                contexts,
                connectionString,
                recorder,
                notificationRecorder,
                tracker,
                checker,
                queue,
                service)
            {
                StartupCleanup = startupCleanup
            };
        }

        public Guid RegisterUpgrade() => Tracker.RegisterOperation(
            OperationType.DownloadHistoryUpgrade,
            DownloadHistoryUpgradeService.OperationName,
            new CancellationTokenSource());

        public DownloadHistoryUpgradeService CreateService(
            Task? startupCleanup = null,
            ILogger<DownloadHistoryUpgradeService>? logger = null) => new(
            Contexts,
            (ISignalRNotificationService)(object)NotificationRecorder,
            Tracker,
            Queue,
            startupCleanup ?? StartupCleanup.Task,
            logger ?? NullLogger<DownloadHistoryUpgradeService>.Instance);

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            foreach (var operation in Tracker.GetActiveOperations().ToList())
            {
                Tracker.CompleteOperation(
                    operation.Id,
                    success: false,
                    error: "Test harness disposed");
            }
            foreach (var operation in Tracker.GetWaitingOperations().ToList())
            {
                Tracker.CancelOperation(operation.Id);
            }
            await Database.DisposeAsync();
        }
    }
}
