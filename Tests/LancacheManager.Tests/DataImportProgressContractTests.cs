using LancacheManager.Models;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LancacheManager.Tests;

public class DataImportProgressContractTests
{
    [Fact]
    public async Task TerminalCleanupRejectsLateImportProgress()
    {
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = new ScheduledRunReporterTests.CapturingNotificationService();
        var controller = new DataMigrationController(NullLogger<DataMigrationController>.Instance,
            notifications, tracker, null!, new ConfigurationBuilder().Build());
        var metrics = new DataImportMetrics();
        var id = tracker.RegisterOperation(OperationType.DataImport, "import", new CancellationTokenSource(),
            metadata: metrics, onTerminalCleanup: metrics.ClearProgress);
        var report = typeof(DataMigrationController).GetMethod("ReportProgressAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)report.Invoke(controller, [id, metrics, 25UL, 100UL, 20UL, 5UL, "progress"])!;
        Assert.Equal(25UL, metrics.CurrentProgress!.Context["processed"]);
        var count = notifications.Events.Count;
        tracker.CompleteOperation(id, false, error: "stopped");
        await (Task)report.Invoke(controller, [id, metrics, 90UL, 100UL, 80UL, 10UL, "late"])!;
        Assert.Null(metrics.CurrentProgress);
        Assert.Equal(count, notifications.Events.Count);
        Assert.Equal("stopped", tracker.GetOperation(id)!.Message);
    }

    /// <summary>
    /// The import's conflict check and registration run under the queue's lock, so a cache clear sent
    /// at the same moment cannot start beside it. A conflict the queue finds is still answered 409 with
    /// that conflict, and nothing is registered or parked.
    /// </summary>
    [Fact]
    public async Task AnImportThatMeetsARunningClearAnswers409AndRegistersNothingAsync()
    {
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        var queue = new OperationWaitlistService(
            tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationWaitlistService>.Instance);
        var clearing = tracker.RegisterOperation(OperationType.CacheClearing, "Cache Clear (All)", new CancellationTokenSource());
        var controller = new DataMigrationController(NullLogger<DataMigrationController>.Instance,
            new ScheduledRunReporterTests.CapturingNotificationService(), tracker, queue, new ConfigurationBuilder().Build());

        var result = await controller.ImportLancacheManagerAsync(
            new DataMigrationImportRequest { ConnectionString = "Host=source;Database=history" },
            CancellationToken.None);

        var refused = Assert.IsType<ConflictObjectResult>(result);
        var conflict = Assert.IsType<OperationConflictResponse>(refused.Value);
        Assert.Equal(clearing, conflict.ActiveOperationId);
        Assert.Empty(tracker.GetActiveOperations(OperationType.DataImport));
        Assert.Empty(tracker.GetWaitingOperations());
    }

    /// <summary>
    /// An import that lands nothing because every record failed ends Failed with the key that names
    /// the failure and the error count as its value, so the card words it in the user's language.
    /// </summary>
    [Fact]
    public async Task AnImportWhoseEveryRecordFailsEndsWithTheKeyAndTheCountAsync()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var target = await TestDatabase.CreateAsync();
        await using (var context = source.Factory.CreateDbContext())
        {
            context.Downloads.Add(new Download
            {
                Service = "steam",
                ClientIp = "10.0.0.1",
                StartTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EndTimeUtc = new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc)
            });
            await context.SaveChangesAsync();
        }
        await using (var context = target.Factory.CreateDbContext())
        {
            // The import reads and writes this column for every record, so without it each one fails.
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Downloads\" DROP COLUMN \"Datasource\" CASCADE");
        }
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        var queue = new OperationWaitlistService(
            tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationWaitlistService>.Instance);
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = target.ConnectionString };
        var notifications = new ScheduledRunReporterTests.CapturingNotificationService();
        var controller = new DataMigrationController(NullLogger<DataMigrationController>.Instance,
            notifications, tracker, queue,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        // Both schemas share one test database, which the import refuses as "the same database" when
        // the host is written the same way; naming the server by its host name keeps the two apart.
        var sourceConnection = new NpgsqlConnectionStringBuilder(source.ConnectionString) { Host = "localhost" };

        await controller.ImportLancacheManagerAsync(
            new DataMigrationImportRequest { ConnectionString = sourceConnection.ConnectionString },
            CancellationToken.None);

        var run = Assert.Single(tracker.GetRuns().Runs);
        Assert.Equal("failed", run.Status);
        Assert.Equal("signalr.dataImport.allRecordsFailed", run.Message);
        var reason = run.FailureReason;
        Assert.NotNull(reason);
        Assert.Equal("signalr.dataImport.allRecordsFailed", reason.StageKey);
        Assert.Equal(1UL, reason.Context["count"]);

        // The live card reads the same key and count the run keeps.
        var sent = await SentCompletionAsync(notifications);
        Assert.False(sent.GetProperty("success").GetBoolean());
        Assert.Equal("signalr.dataImport.allRecordsFailed", StageKeyOf(sent));
        Assert.Equal(1UL, sent.GetProperty("context").GetProperty("count").GetUInt64());
    }

    /// <summary>
    /// A finished import sends the key of its summary; the card fills in the counts from the
    /// count fields sent beside it.
    /// </summary>
    [Fact]
    public async Task AFinishedImportSendsItsSummaryKeyAndCountsAsync()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var target = await TestDatabase.CreateAsync();
        await using (var context = source.Factory.CreateDbContext())
        {
            context.Downloads.Add(new Download
            {
                Service = "steam",
                ClientIp = "10.0.0.2",
                StartTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EndTimeUtc = new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc)
            });
            await context.SaveChangesAsync();
        }
        await using (var context = target.Factory.CreateDbContext())
        {
            // Migrated databases keep this default, which the generated test schema omits; the
            // import's insert leaves the column out, so without it every record fails.
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Downloads\" ALTER COLUMN \"IsEvicted\" SET DEFAULT FALSE");
        }
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        var queue = new OperationWaitlistService(
            tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationWaitlistService>.Instance);
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = target.ConnectionString };
        var notifications = new ScheduledRunReporterTests.CapturingNotificationService();
        var controller = new DataMigrationController(NullLogger<DataMigrationController>.Instance,
            notifications, tracker, queue,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var sourceConnection = new NpgsqlConnectionStringBuilder(source.ConnectionString) { Host = "localhost" };

        await controller.ImportLancacheManagerAsync(
            new DataMigrationImportRequest { ConnectionString = sourceConnection.ConnectionString },
            CancellationToken.None);

        var sent = await SentCompletionAsync(notifications);
        Assert.True(sent.GetProperty("success").GetBoolean());
        Assert.Equal("management.database.import.completed", StageKeyOf(sent));
        Assert.Equal(1UL, sent.GetProperty("recordsImported").GetUInt64());
        Assert.Equal(0UL, sent.GetProperty("recordsSkipped").GetUInt64());
        Assert.Equal(0UL, sent.GetProperty("recordsErrors").GetUInt64());
    }

    /// <summary>
    /// A force stop ends the import from outside it, so the import never writes a summary. The
    /// completion sends the cancel key and no sentence in place of the missing summary.
    /// </summary>
    [Fact]
    public async Task AForceStoppedImportSendsTheCancelKeyAndNoSentenceAsync()
    {
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        var queue = new OperationWaitlistService(
            tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationWaitlistService>.Instance);
        var notifications = new ScheduledRunReporterTests.CapturingNotificationService();
        var controller = new DataMigrationController(NullLogger<DataMigrationController>.Instance,
            notifications, tracker, queue, new ConfigurationBuilder().Build());
        var start = typeof(DataMigrationController).GetMethod("StartImportAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var starting = (Task)start.Invoke(controller, ["LancacheManager Import", CancellationToken.None])!;
        await starting;
        var started = starting.GetType().GetProperty("Result")!.GetValue(starting)!;
        var id = (Guid?)started.GetType().GetField("Item1")!.GetValue(started);
        Assert.NotNull(id);

        // What the force stop does when the import has not ended the run itself.
        tracker.CompleteOperation(id.Value, success: false, canceled: true);

        var sent = await SentCompletionAsync(notifications);
        Assert.True(sent.GetProperty("canceled").GetBoolean());
        Assert.Equal("signalr.dataImport.canceled", StageKeyOf(sent));
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("message").ValueKind);
    }

    /// <summary>
    /// A source connection string the import cannot read is refused with the same key the run ends with.
    /// </summary>
    [Fact]
    public async Task AnInvalidSourceIsRefusedWithTheKeyTheRunEndsWithAsync()
    {
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        var queue = new OperationWaitlistService(
            tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationWaitlistService>.Instance);
        var notifications = new ScheduledRunReporterTests.CapturingNotificationService();
        var controller = new DataMigrationController(NullLogger<DataMigrationController>.Instance,
            notifications, tracker, queue, new ConfigurationBuilder().Build());

        var result = await controller.ImportLancacheManagerAsync(
            new DataMigrationImportRequest { ConnectionString = "Port=5432" },
            CancellationToken.None);

        var refused = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("errors.dataImport.invalidSource", Assert.IsType<ErrorResponse>(refused.Value).StageKey);
        Assert.Equal("errors.dataImport.invalidSource", Assert.Single(tracker.GetRuns().Runs).Message);

        // The card reads the same key; the import wrote no summary, so none is sent.
        var sent = await SentCompletionAsync(notifications);
        Assert.False(sent.GetProperty("success").GetBoolean());
        Assert.Equal("errors.dataImport.invalidSource", StageKeyOf(sent));
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("message").ValueKind);
    }

    // The completion as the browser receives it: the hub writes payloads as camelCase JSON.
    private static async Task<JsonElement> SentCompletionAsync(ScheduledRunReporterTests.CapturingNotificationService notifications)
    {
        var sent = await notifications.WhenEventAsync(SignalREvents.DataImportComplete).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(sent.Payload);
        return JsonSerializer.SerializeToElement(sent.Payload, sent.Payload.GetType(), JsonSerializerOptions.Web);
    }

    private static string? StageKeyOf(JsonElement sent) =>
        sent.TryGetProperty("stageKey", out var stageKey) ? stageKey.GetString() : null;

    [Fact]
    public void StructuredSnapshotsCarryRealProcessedAndTotalValues()
    {
        var metrics = new DataImportMetrics();
        var starting = metrics.CaptureProgress(
            "signalr.dataImport.starting", 0, new Dictionary<string, object?>());
        var progress = metrics.CaptureProgress(
            "signalr.dataImport.progress",
            25,
            new Dictionary<string, object?> { ["processed"] = 25UL, ["total"] = 100UL });

        Assert.Equal("signalr.dataImport.starting", starting.StageKey);
        Assert.Equal(25UL, progress.Context["processed"]);
        Assert.Equal(100UL, progress.Context["total"]);
        Assert.Same(progress, metrics.CurrentProgress);

        metrics.ClearProgress();
        Assert.Null(metrics.CurrentProgress);
    }
}
