using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LancacheManager.Tests;

/// <summary>
/// A user types a batch size of 1 into the database import form, which the box accepts, and imports a few hundred
/// downloads. Every browser on the page gets each progress message the import sends, so the import must send them at
/// most once per send interval while it runs, as the removals do, and still show the final count.
/// </summary>
public sealed class SmallBatchImportProgressRateTests
{
    private const int Records = 300;

    [Fact]
    public async Task AnImportWithABatchSizeOfOneSendsAFewProgressMessagesAndItsFinalCountAsync()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var target = await TestDatabase.CreateAsync();
        var firstStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var context = source.Factory.CreateDbContext())
        {
            for (var record = 0; record < Records; record++)
            {
                context.Downloads.Add(new Download
                {
                    Service = "steam",
                    ClientIp = $"10.0.{record / 250}.{record % 250 + 1}",
                    StartTimeUtc = firstStart.AddMinutes(record),
                    EndTimeUtc = firstStart.AddMinutes(record).AddSeconds(30)
                });
            }

            await context.SaveChangesAsync();
        }

        await using (var context = target.Factory.CreateDbContext())
        {
            // Migrated databases keep this default, which the generated test schema omits; the import's insert
            // leaves the column out, so without it every record fails.
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
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance), null!);
        // Both schemas share one test database, which the import refuses as "the same database" when the host is
        // written the same way; naming the server by its host name keeps the two apart.
        var sourceConnection = new NpgsqlConnectionStringBuilder(source.ConnectionString) { Host = "localhost" };

        var importing = Stopwatch.StartNew();
        await controller.ImportLancacheManagerAsync(
            new DataMigrationImportRequest { ConnectionString = sourceConnection.ConnectionString, BatchSize = 1 },
            CancellationToken.None);
        await notifications.WhenEventAsync(SignalREvents.DataImportComplete).WaitAsync(TimeSpan.FromSeconds(30));
        importing.Stop();

        // The progress messages as the browser receives them: the hub writes payloads as camelCase JSON.
        var progress = notifications.Events
            .Where(message => message.EventName == SignalREvents.DataImportProgress && message.Payload is not null)
            .Select(message => JsonSerializer.SerializeToElement(
                message.Payload, message.Payload!.GetType(), JsonSerializerOptions.Web))
            .ToList();

        // The starting message, one for each full send interval the import took, and the final count.
        var allowed = 2 + (int)(importing.ElapsedMilliseconds / RustProcessHelper.ProgressEmitMinIntervalMs);
        Assert.True(progress.Count <= allowed,
            $"An import of {Records} records with a batch size of 1 took {importing.ElapsedMilliseconds} ms and sent " +
            $"{progress.Count} progress messages to every browser; at most one per " +
            $"{RustProcessHelper.ProgressEmitMinIntervalMs} ms, {allowed} in that time, may go out");

        Assert.NotEmpty(progress);
        var last = progress[^1];
        Assert.Equal((ulong)Records, last.GetProperty("recordsProcessed").GetUInt64());
        Assert.Equal((ulong)Records, last.GetProperty("recordsImported").GetUInt64());
    }
}
